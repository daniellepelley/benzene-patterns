// Package eventstore is an app-local, DynamoDB-backed event store for the Trade Ledger.
//
// It exists ONLY because benzene-go has no event-sourcing package. In .NET this is framework code
// - Benzene.EventSourcing (IEventStore, EventEnvelope, optimistic-concurrency AppendAsync) plus
// Benzene.EventSourcing.DynamoDb (DynamoDbEventStore, the pk/version/eventType/payload/timestamp
// item shape) - and the Trade Ledger simply inherits it. No non-.NET port has lifted event
// sourcing into the framework yet, so every other language re-implements this same ~150 lines of
// "conditional-write append + query read" by hand. This package is that hand-rolled equivalent
// for Go, documented as app-local per PARITY-FINDINGS.md §3.1 ("Event sourcing is a .NET-only
// capability... this repo's ports implement the store as app-local code").
//
// The DynamoDB item shape is byte-for-byte the shape the .NET DynamoDbEventStore writes and the
// shared Terraform table (real-time-risk/deploy/terraform/dynamodb.tf) provisions, so the store is
// wire-compatible across languages against one physical table:
//
//	pk        (S, partition) = stream / book id
//	version   (N, sort)      = 1-based sequence within the stream
//	eventType (S)            = the event-type discriminator (e.g. "TradeBooked")
//	payload   (S)            = the event body as a JSON string
//	timestamp (S)            = RFC3339 append time
package eventstore

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb/types"
)

// Event is one event to append: its type discriminator and its already-serialized JSON payload -
// the Go counterpart of .NET's EventEnvelope.
type Event struct {
	EventType string
	Payload   string
}

// StoredEvent is one event as read back from the store, with its assigned stream position and
// append time.
type StoredEvent struct {
	StreamID  string
	Version   int64
	EventType string
	Payload   string
	Timestamp time.Time
}

// ErrConcurrency is returned by Append when the optimistic-concurrency condition fails: some
// other writer already appended at one of the versions this call tried to claim (the ledger's
// TransactWriteItems was cancelled). The .NET analogue is EventStoreConcurrencyException. A caller
// that wants last-writer-wins retries would re-read the stream and retry the append.
var ErrConcurrency = errors.New("eventstore: optimistic concurrency conflict (stream advanced concurrently)")

// Store is the event-sourcing read/append surface the Trade Ledger depends on. Declaring it as an
// interface (rather than the concrete DynamoStore) is what lets the handler's tests run against an
// in-memory fake with no DynamoDB, per the task's "event-store integration against real DynamoDB
// is out of scope for local unit tests" note.
type Store interface {
	// Read returns every event on streamID in ascending version order (empty for an unknown stream).
	Read(ctx context.Context, streamID string) ([]StoredEvent, error)
	// Append writes events at versions expectedVersion+1, +2, ... atomically, failing with
	// ErrConcurrency if any of those versions already exists. It returns the new highest version.
	Append(ctx context.Context, streamID string, expectedVersion int64, events []Event) (newVersion int64, err error)
}

// DynamoStore is the DynamoDB-backed Store. It is safe for concurrent use (the AWS SDK client is).
type DynamoStore struct {
	client    *dynamodb.Client
	tableName string
}

// NewDynamoStore builds a DynamoStore over client for tableName.
func NewDynamoStore(client *dynamodb.Client, tableName string) *DynamoStore {
	return &DynamoStore{client: client, tableName: tableName}
}

var _ Store = (*DynamoStore)(nil)

// Read queries `pk = :pk` with ScanIndexForward=true so events come back in ascending version
// order - the stream's history, oldest first.
func (s *DynamoStore) Read(ctx context.Context, streamID string) ([]StoredEvent, error) {
	out, err := s.client.Query(ctx, &dynamodb.QueryInput{
		TableName:              aws.String(s.tableName),
		KeyConditionExpression: aws.String("pk = :pk"),
		ExpressionAttributeValues: map[string]types.AttributeValue{
			":pk": &types.AttributeValueMemberS{Value: streamID},
		},
		ScanIndexForward: aws.Bool(true),
	})
	if err != nil {
		return nil, fmt.Errorf("eventstore: read stream %q: %w", streamID, err)
	}

	events := make([]StoredEvent, 0, len(out.Items))
	for _, item := range out.Items {
		stored, err := itemToStoredEvent(streamID, item)
		if err != nil {
			return nil, err
		}
		events = append(events, stored)
	}
	return events, nil
}

// Append writes each event as its own item at the next version, all in one TransactWriteItems so
// the append is atomic. Every Put carries ConditionExpression `attribute_not_exists(pk)`: on a
// Put keyed by (pk, version) that condition is true only when no item with that exact key exists,
// which is the optimistic-concurrency guard - if a concurrent writer already claimed the version,
// the whole transaction is cancelled and we surface ErrConcurrency. This mirrors the .NET
// DynamoDbEventStore's TransactWriteItems + attribute_not_exists(#pk) append.
func (s *DynamoStore) Append(ctx context.Context, streamID string, expectedVersion int64, events []Event) (int64, error) {
	if len(events) == 0 {
		return expectedVersion, nil
	}

	timestamp := time.Now().UTC().Format(time.RFC3339)
	writes := make([]types.TransactWriteItem, 0, len(events))
	version := expectedVersion
	for _, event := range events {
		version++
		writes = append(writes, types.TransactWriteItem{
			Put: &types.Put{
				TableName: aws.String(s.tableName),
				Item: map[string]types.AttributeValue{
					"pk":        &types.AttributeValueMemberS{Value: streamID},
					"version":   &types.AttributeValueMemberN{Value: strconv.FormatInt(version, 10)},
					"eventType": &types.AttributeValueMemberS{Value: event.EventType},
					"payload":   &types.AttributeValueMemberS{Value: event.Payload},
					"timestamp": &types.AttributeValueMemberS{Value: timestamp},
				},
				// attribute_not_exists(pk) => "no item with this (pk, version) key yet" => optimistic lock.
				ConditionExpression: aws.String("attribute_not_exists(pk)"),
			},
		})
	}

	_, err := s.client.TransactWriteItems(ctx, &dynamodb.TransactWriteItemsInput{TransactItems: writes})
	if err != nil {
		var cancelled *types.TransactionCanceledException
		if errors.As(err, &cancelled) {
			return 0, fmt.Errorf("%w: stream %q at expected version %d: %v", ErrConcurrency, streamID, expectedVersion, err)
		}
		return 0, fmt.Errorf("eventstore: append to stream %q: %w", streamID, err)
	}
	return version, nil
}

// itemToStoredEvent decodes one DynamoDB item into a StoredEvent, tolerating a missing/typeless
// attribute by treating it as its zero value rather than failing the whole read (a malformed row
// is a data problem, not a reason to 500 every query of the stream).
func itemToStoredEvent(streamID string, item map[string]types.AttributeValue) (StoredEvent, error) {
	stored := StoredEvent{StreamID: streamID}

	if v, ok := item["version"].(*types.AttributeValueMemberN); ok {
		version, err := strconv.ParseInt(v.Value, 10, 64)
		if err != nil {
			return StoredEvent{}, fmt.Errorf("eventstore: stream %q has non-numeric version %q: %w", streamID, v.Value, err)
		}
		stored.Version = version
	}
	if v, ok := item["eventType"].(*types.AttributeValueMemberS); ok {
		stored.EventType = v.Value
	}
	if v, ok := item["payload"].(*types.AttributeValueMemberS); ok {
		stored.Payload = v.Value
	}
	if v, ok := item["timestamp"].(*types.AttributeValueMemberS); ok {
		if ts, err := time.Parse(time.RFC3339, v.Value); err == nil {
			stored.Timestamp = ts
		}
	}
	return stored, nil
}
