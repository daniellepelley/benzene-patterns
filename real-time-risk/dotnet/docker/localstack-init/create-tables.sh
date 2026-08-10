#!/bin/sh
# Runs automatically once LocalStack is ready (LocalStack's init-hook mechanism: any script under
# /etc/localstack/init/ready.d/ is executed after the emulated services are up). Provisions the
# "trades" table Benzene.EventSourcing.DynamoDb's DynamoDbEventStore writes to (composite key: string
# partition "pk" + numeric sort "version", matching that package's defaults - see
# real-time-risk/dotnet/TradeLedger/StartUp.cs), with a stream enabled so RiskReadModels'
# TradeStreamProjector has something to consume.
set -eu

awslocal dynamodb create-table \
  --table-name trades \
  --attribute-definitions AttributeName=pk,AttributeType=S AttributeName=version,AttributeType=N \
  --key-schema AttributeName=pk,KeyType=HASH AttributeName=version,KeyType=RANGE \
  --billing-mode PAY_PER_REQUEST \
  --stream-specification StreamEnabled=true,StreamViewType=NEW_AND_OLD_IMAGES

echo "trades table created with streams enabled"
