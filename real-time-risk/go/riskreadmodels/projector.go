package riskreadmodels

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"strconv"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb"
	ddbtypes "github.com/aws/aws-sdk-go-v2/service/dynamodb/types"
	"github.com/aws/aws-sdk-go-v2/service/dynamodbstreams"
	streamstypes "github.com/aws/aws-sdk-go-v2/service/dynamodbstreams/types"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
)

// TradeStreamProjector consumes the Trade Ledger's DynamoDB Stream and folds each TradeBooked event
// into BookPositionsStore. It is the local/Docker Compose substitute for what, on AWS, is a real
// Lambda DynamoDB-Streams event-source mapping.
//
// PRODUCTION SHAPE: on AWS this projection would be an awsdynamodb.Handler (topic
// "{table}:INSERT") hosted in a Lambda via awslambda.Start - the exact design the .NET port
// documents (Benzene.Aws.Lambda.DynamoDb, [Message("trades:INSERT")]). Emulating a Lambda +
// event-source mapping locally means a Lambda executor and a lot of Docker-in-Docker machinery for
// little fidelity gain, so this poller reads the same stream directly with the AWS SDK and applies
// records to the same store. The wire shape (topic + JSON body) is identical; swapping in the real
// Lambda-hosted handler later is a hosting change, not a rewrite.
//
// Deliberate demo simplifications (not production streaming code): shard iterators are held only in
// memory (no checkpoint store, so a restart re-reads from TRIM_HORIZON - safe because Apply is
// idempotent per (book, version)), and shard splits/merges are handled by simply re-describing the
// stream each loop rather than walking shard lineage (fine for a single low-volume demo table).
// This mirrors .NET's TradeStreamProjector line for line.
type TradeStreamProjector struct {
	dynamo    *dynamodb.Client
	streams   *dynamodbstreams.Client
	store     *BookPositionsStore
	tableName string
}

const (
	pollInterval      = 1 * time.Second
	tableWaitInterval = 2 * time.Second
)

// NewTradeStreamProjector builds a projector over the table + streams clients, writing into store.
func NewTradeStreamProjector(dynamo *dynamodb.Client, streams *dynamodbstreams.Client, store *BookPositionsStore, tableName string) *TradeStreamProjector {
	return &TradeStreamProjector{dynamo: dynamo, streams: streams, store: store, tableName: tableName}
}

// Run polls the stream until ctx is cancelled. It is meant to run in its own goroutine alongside
// the HTTP server. It never returns an error: transient failures are logged and retried, matching
// the .NET BackgroundService's "log and keep polling" behaviour.
func (p *TradeStreamProjector) Run(ctx context.Context) {
	streamArn, ok := p.waitForStreamArn(ctx)
	if !ok {
		return // ctx cancelled before the table/stream appeared.
	}
	log.Printf("riskreadmodels: projecting from stream %s", streamArn)

	shardIterators := make(map[string]*string)
	pollCount := 0

	for {
		if ctx.Err() != nil {
			return
		}
		pollCount++
		if err := p.poll(ctx, streamArn, shardIterators); err != nil && !errors.Is(err, context.Canceled) {
			// The loop must not die silently on a transient failure - log and keep polling.
			log.Printf("riskreadmodels: poll %d failed - will retry: %v", pollCount, err)
		}

		select {
		case <-ctx.Done():
			return
		case <-time.After(pollInterval):
		}
	}
}

// poll does one pass: discover new shards (TRIM_HORIZON), then drain each tracked shard's records.
func (p *TradeStreamProjector) poll(ctx context.Context, streamArn string, shardIterators map[string]*string) error {
	desc, err := p.streams.DescribeStream(ctx, &dynamodbstreams.DescribeStreamInput{StreamArn: aws.String(streamArn)})
	if err != nil {
		return err
	}

	for _, shard := range desc.StreamDescription.Shards {
		shardID := aws.ToString(shard.ShardId)
		if _, tracked := shardIterators[shardID]; tracked {
			continue
		}
		it, err := p.streams.GetShardIterator(ctx, &dynamodbstreams.GetShardIteratorInput{
			StreamArn:         aws.String(streamArn),
			ShardId:           shard.ShardId,
			ShardIteratorType: streamstypes.ShardIteratorTypeTrimHorizon,
		})
		if err != nil {
			return err
		}
		shardIterators[shardID] = it.ShardIterator
		log.Printf("riskreadmodels: started tracking shard %s", shardID)
	}

	for shardID, iterator := range shardIterators {
		if iterator == nil {
			// A nil NextShardIterator means the shard is closed and fully drained - nothing left.
			continue
		}
		records, err := p.streams.GetRecords(ctx, &dynamodbstreams.GetRecordsInput{ShardIterator: iterator})
		if err != nil {
			return err
		}
		if len(records.Records) > 0 {
			log.Printf("riskreadmodels: shard %s returned %d record(s)", shardID, len(records.Records))
		}
		for i := range records.Records {
			p.apply(records.Records[i])
		}
		shardIterators[shardID] = records.NextShardIterator
	}
	return nil
}

// apply folds one stream record into the store if it is a TradeBooked INSERT. Anything else (a
// MODIFY/REMOVE, or a record without the expected attributes) is ignored - the ledger only ever
// appends, so INSERT is the only change type that carries a new event.
func (p *TradeStreamProjector) apply(record streamstypes.Record) {
	if record.EventName != streamstypes.OperationTypeInsert {
		return
	}
	if record.Dynamodb == nil || record.Dynamodb.NewImage == nil {
		return
	}
	image := record.Dynamodb.NewImage

	if eventType, ok := image["eventType"].(*streamstypes.AttributeValueMemberS); !ok || eventType.Value != contracts.TradeBookedEventType {
		return
	}
	payloadAttr, ok := image["payload"].(*streamstypes.AttributeValueMemberS)
	if !ok || payloadAttr.Value == "" {
		return
	}
	versionAttr, ok := image["version"].(*streamstypes.AttributeValueMemberN)
	if !ok {
		return
	}
	version, err := strconv.ParseInt(versionAttr.Value, 10, 64)
	if err != nil {
		return
	}

	var trade contracts.TradeBooked
	if err := json.Unmarshal([]byte(payloadAttr.Value), &trade); err != nil {
		log.Printf("riskreadmodels: skipping unparseable TradeBooked payload at version %d: %v", version, err)
		return
	}

	p.store.Apply(trade, version)
	log.Printf("riskreadmodels: projected trade %s into book %s at version %d", trade.TradeId, trade.Book, version)
}

// waitForStreamArn polls DescribeTable until the table exists and reports a LatestStreamArn - the
// compose bootstrap (the Trade Ledger creating the table) may still be running when this service
// starts. Returns ok=false only if ctx is cancelled first.
func (p *TradeStreamProjector) waitForStreamArn(ctx context.Context) (string, bool) {
	for {
		out, err := p.dynamo.DescribeTable(ctx, &dynamodb.DescribeTableInput{TableName: aws.String(p.tableName)})
		if err != nil {
			var notFound *ddbtypes.ResourceNotFoundException
			if !errors.As(err, &notFound) {
				log.Printf("riskreadmodels: describing %q table failed - will retry: %v", p.tableName, err)
			}
		} else if arn := aws.ToString(out.Table.LatestStreamArn); arn != "" {
			return arn, true
		}

		log.Printf("riskreadmodels: waiting for the %q table (and its stream) to be provisioned...", p.tableName)
		select {
		case <-ctx.Done():
			return "", false
		case <-time.After(tableWaitInterval):
		}
	}
}
