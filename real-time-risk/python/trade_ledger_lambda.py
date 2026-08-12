"""AWS Lambda entry point for the Trade Ledger service.

    handler = trade_ledger_lambda.handler   # the callable Lambda invokes

The *same* :class:`~trade_ledger.startup.TradeLedgerStartUp` composition root the local ASGI host
(`trade_ledger/main.py`) boots from, hosted on benzene-python's :class:`~benzene.aws.AwsLambdaApp`
behind an API Gateway HTTP API (payload format 2.0) instead of uvicorn. The ``trade:book`` handler is
tagged ``@http_endpoint("POST", "/trades")``, so ``AwsLambdaApp`` routes ``POST /trades`` straight to
it via the HTTP binding — the wire contract (topic + camelCase JSON body) is byte-for-byte what the
Compose slice serves.

Two differences from `trade_ledger/main.py`, both because this runs against real AWS:

1. **No table provisioning.** On AWS the ledger table (with its stream) is pre-provisioned by the
   shared Terraform (`../deploy/terraform/dynamodb.tf`), so — unlike the ASGI host, which calls
   :func:`~trade_ledger.provisioning.ensure_trades_table_exists` on startup because DynamoDB Local
   has no external provisioner — this module provisions nothing. The Lambda execution role is not
   granted ``dynamodb:CreateTable`` either.
2. **No lifespan wrapper.** There is no long-running server to hang startup/shutdown off, and nothing
   to start (the ASGI host used :class:`~asgi_lifespan.LifespanApp` only to run provisioning); the
   Lambda simply builds the app once at import (cold start) and serves invocations.

``DynamoDbEventStore`` is built with :func:`~trade_ledger.dynamodb.create_dynamodb_client`, which
honours ``DYNAMODB_SERVICE_URL`` only for the Compose path; unset (the Lambda case) boto3 falls back
to the default credential/region chain — the function's execution role. Nothing Lambda-specific to
wire.
"""

from __future__ import annotations

from benzene.aws import AwsLambdaApp, to_lambda_handler
from benzene.core import build_application

from trade_ledger.startup import TradeLedgerStartUp

# Build once per cold start: register the event store, resolve it, and wire POST /trades.
_definition, _scope = build_application(TradeLedgerStartUp)

#: The ``handler(event, context)`` callable AWS Lambda invokes (its configured handler is
#: ``trade_ledger_lambda.handler``).
handler = to_lambda_handler(AwsLambdaApp.from_definition(_definition))
