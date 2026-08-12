// Package dynamoclient builds the DynamoDB table and streams clients both services use, making the
// "DynamoDB Local vs real AWS" decision once instead of in each main. It is the Go counterpart of
// the .NET port's TradeLedger/StartUp.cs CreateDynamoDbClient and RiskReadModels/DynamoDbClients.
//
// When DYNAMODB_SERVICE_URL is set (the Docker Compose local-dev target), the clients point at
// that endpoint with throwaway static credentials. When it is empty (a real AWS deployment), they
// fall back to the default credential/region chain from the environment/IAM role. The aws-sdk-go-v2
// BaseEndpoint override is the clean equivalent of the .NET ServiceURL story: it overrides only the
// endpoint, never the signing region, so it does not trip the .NET SDK's "RegionEndpoint makes
// ServiceURL be ignored" pitfall those files document at length - a whole class of bug that simply
// does not exist in the v2 Go SDK's endpoint model.
package dynamoclient

import (
	"context"
	"fmt"
	"os"

	"github.com/aws/aws-sdk-go-v2/aws"
	awsconfig "github.com/aws/aws-sdk-go-v2/config"
	"github.com/aws/aws-sdk-go-v2/credentials"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb"
	"github.com/aws/aws-sdk-go-v2/service/dynamodbstreams"
)

const (
	// ServiceURLEnv is the endpoint-override env var; empty means "use real AWS".
	ServiceURLEnv = "DYNAMODB_SERVICE_URL"
	// TableNameEnv names the ledger's event table; DefaultTableName is used when unset.
	TableNameEnv = "TRADES_TABLE_NAME"
	// DefaultTableName is the table name both services default to (matches the .NET port).
	DefaultTableName = "trades"

	region = "us-east-1"
	// DynamoDB Local 2.0+ validates the access-key format, so a short placeholder like "local" is
	// rejected - these are the exact placeholders AWS's own DynamoDB Local docs use.
	dummyAccessKeyID     = "DUMMYIDEXAMPLE"
	dummySecretAccessKey = "DUMMYEXAMPLEKEY"
)

// TableName returns the configured event-table name (TRADES_TABLE_NAME), or DefaultTableName.
func TableName() string {
	if name := os.Getenv(TableNameEnv); name != "" {
		return name
	}
	return DefaultTableName
}

// ServiceURL returns the configured endpoint override (DYNAMODB_SERVICE_URL), or "" for real AWS.
func ServiceURL() string {
	return os.Getenv(ServiceURLEnv)
}

// loadConfig builds the base aws.Config: static throwaway credentials + a fixed region when an
// endpoint override is set (DynamoDB Local), or the default chain otherwise.
func loadConfig(ctx context.Context, serviceURL string) (aws.Config, error) {
	if serviceURL == "" {
		cfg, err := awsconfig.LoadDefaultConfig(ctx)
		if err != nil {
			return aws.Config{}, fmt.Errorf("dynamoclient: load default AWS config: %w", err)
		}
		return cfg, nil
	}
	cfg, err := awsconfig.LoadDefaultConfig(ctx,
		awsconfig.WithRegion(region),
		awsconfig.WithCredentialsProvider(credentials.NewStaticCredentialsProvider(dummyAccessKeyID, dummySecretAccessKey, "")),
	)
	if err != nil {
		return aws.Config{}, fmt.Errorf("dynamoclient: load AWS config for %s: %w", serviceURL, err)
	}
	return cfg, nil
}

// NewDynamoDB builds the table client, honouring the DYNAMODB_SERVICE_URL endpoint override.
func NewDynamoDB(ctx context.Context) (*dynamodb.Client, error) {
	serviceURL := ServiceURL()
	cfg, err := loadConfig(ctx, serviceURL)
	if err != nil {
		return nil, err
	}
	return dynamodb.NewFromConfig(cfg, func(o *dynamodb.Options) {
		if serviceURL != "" {
			o.BaseEndpoint = aws.String(serviceURL)
		}
	}), nil
}

// NewStreams builds the DynamoDB Streams client. DynamoDB Local serves the Streams API on the same
// endpoint as the table API, so the same override applies.
func NewStreams(ctx context.Context) (*dynamodbstreams.Client, error) {
	serviceURL := ServiceURL()
	cfg, err := loadConfig(ctx, serviceURL)
	if err != nil {
		return nil, err
	}
	return dynamodbstreams.NewFromConfig(cfg, func(o *dynamodbstreams.Options) {
		if serviceURL != "" {
			o.BaseEndpoint = aws.String(serviceURL)
		}
	}), nil
}
