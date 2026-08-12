"""DynamoDB client construction for the Trade Ledger (DynamoDB Local vs. real AWS).

One place decides the DynamoDB-Local-vs-real-AWS wiring, mirroring .NET ``TradeLedger/StartUp.cs``'s
``CreateDynamoDbClient``. When ``DYNAMODB_SERVICE_URL`` is set (the Docker Compose local-dev target)
the client points at that endpoint with throwaway credentials; otherwise it falls back to the default
client (region + credential chain from the environment/IAM role) for a real AWS deployment.

Unlike the .NET SDK, boto3 has no "setting a region silently overrides the endpoint" pitfall
(aws/aws-sdk-net#1781): ``endpoint_url`` is always honored, and ``region_name`` only supplies the
SigV4 signing region — so the workaround the .NET file documents (``AuthenticationRegion`` instead of
``RegionEndpoint``) has no Python analogue to reproduce.
"""

from __future__ import annotations

import os
from typing import Any

# DynamoDB Local 2.0+ validates the access-key *format* more strictly than earlier versions — a short
# placeholder like "local" is rejected. These are the exact placeholders AWS's own DynamoDB Local docs
# use, matching .NET ``DynamoDbClients``.
_LOCAL_ACCESS_KEY_ID = "DUMMYIDEXAMPLE"
_LOCAL_SECRET_ACCESS_KEY = "DUMMYEXAMPLEKEY"
_LOCAL_REGION = "us-east-1"


def trades_table_name() -> str:
    """The ledger table name from ``TRADES_TABLE_NAME`` (default ``trades``)."""
    return os.environ.get("TRADES_TABLE_NAME", "trades")


def _service_url() -> str | None:
    return os.environ.get("DYNAMODB_SERVICE_URL") or None


def create_dynamodb_client() -> Any:
    """Build the DynamoDB (table API) client, honoring ``DYNAMODB_SERVICE_URL`` when present."""
    return _create_client("dynamodb")


def create_streams_client() -> Any:
    """Build the DynamoDB **Streams** API client (same endpoint as the table API on DynamoDB Local)."""
    return _create_client("dynamodbstreams")


def _create_client(service_name: str) -> Any:
    import boto3

    service_url = _service_url()
    if not service_url:
        return boto3.client(service_name)
    return boto3.client(
        service_name,
        endpoint_url=service_url,
        region_name=_LOCAL_REGION,
        aws_access_key_id=_LOCAL_ACCESS_KEY_ID,
        aws_secret_access_key=_LOCAL_SECRET_ACCESS_KEY,
    )
