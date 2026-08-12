package tradeledger

import (
	"context"
	"errors"
	"log"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb"
	"github.com/aws/aws-sdk-go-v2/service/dynamodb/types"
)

// provisioningMaxAttempts and provisioningRetryInterval give ~60s of headroom (30 * 2s) for the
// window right after `docker compose up` when DynamoDB Local may not have finished starting - the
// same budget as the .NET DynamoDbTableProvisioning.
const (
	provisioningMaxAttempts   = 30
	provisioningRetryInterval = 2 * time.Second
)

// EnsureTradesTableExists creates the ledger's event table (with its stream enabled) if it does
// not already exist, idempotently and with retries. DynamoDB Local has no bundled provisioning
// tool and there is no separate init container (see docker-compose.yml), so the service that owns
// the table provisions it itself at startup. The key schema and StreamSpecification match the
// shared Terraform table and the .NET DynamoDbTableProvisioning exactly:
//
//	pk (HASH, S) / version (RANGE, N), PAY_PER_REQUEST, stream NEW_AND_OLD_IMAGES.
func EnsureTradesTableExists(ctx context.Context, client *dynamodb.Client, tableName string) error {
	input := &dynamodb.CreateTableInput{
		TableName:   aws.String(tableName),
		BillingMode: types.BillingModePayPerRequest,
		AttributeDefinitions: []types.AttributeDefinition{
			{AttributeName: aws.String("pk"), AttributeType: types.ScalarAttributeTypeS},
			{AttributeName: aws.String("version"), AttributeType: types.ScalarAttributeTypeN},
		},
		KeySchema: []types.KeySchemaElement{
			{AttributeName: aws.String("pk"), KeyType: types.KeyTypeHash},
			{AttributeName: aws.String("version"), KeyType: types.KeyTypeRange},
		},
		StreamSpecification: &types.StreamSpecification{
			StreamEnabled:  aws.Bool(true),
			StreamViewType: types.StreamViewTypeNewAndOldImages,
		},
	}

	for attempt := 1; attempt <= provisioningMaxAttempts; attempt++ {
		_, err := client.CreateTable(ctx, input)
		if err == nil {
			log.Printf("tradeledger: created the %q table (streams enabled)", tableName)
			return nil
		}

		var inUse *types.ResourceInUseException
		if errors.As(err, &inUse) {
			// Already provisioned - a previous run, or this process restarting. Nothing to do.
			log.Printf("tradeledger: the %q table already exists", tableName)
			return nil
		}

		if attempt == provisioningMaxAttempts {
			return err
		}
		log.Printf("tradeledger: could not provision the %q table yet (attempt %d/%d) - retrying: %v",
			tableName, attempt, provisioningMaxAttempts, err)

		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(provisioningRetryInterval):
		}
	}
	return nil
}
