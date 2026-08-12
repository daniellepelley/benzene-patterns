"""Creates the ledger's DynamoDB table (stream enabled) at startup if it doesn't already exist.

DynamoDB Local (the Docker Compose local-dev target) has no bundled provisioning tool and no separate
init container in this repo, so the service that *owns* the table provisions it itself — idempotently,
with retries for the window right after ``docker compose up`` when the database container may not have
finished starting yet. Mirrors .NET ``TradeLedger/DynamoDbTableProvisioning.cs``.

The composite key (``pk`` HASH / ``version`` RANGE), ``PAY_PER_REQUEST`` billing, and the
``NEW_AND_OLD_IMAGES`` stream match the app-local event store item shape (`event_store.py`) and the
shared Terraform table (`real-time-risk/deploy/terraform/dynamodb.tf`).
"""

from __future__ import annotations

import logging
import time
from typing import Any

_MAX_ATTEMPTS = 30
_RETRY_INTERVAL_SECONDS = 2.0

logger = logging.getLogger(__name__)


def ensure_trades_table_exists(client: Any, table_name: str) -> None:
    """Create the ledger table if absent; a no-op if it already exists. Retries ~60s for warmup."""
    for attempt in range(1, _MAX_ATTEMPTS + 1):
        try:
            client.create_table(
                TableName=table_name,
                AttributeDefinitions=[
                    {"AttributeName": "pk", "AttributeType": "S"},
                    {"AttributeName": "version", "AttributeType": "N"},
                ],
                KeySchema=[
                    {"AttributeName": "pk", "KeyType": "HASH"},
                    {"AttributeName": "version", "KeyType": "RANGE"},
                ],
                BillingMode="PAY_PER_REQUEST",
                StreamSpecification={
                    "StreamEnabled": True,
                    "StreamViewType": "NEW_AND_OLD_IMAGES",
                },
            )
            logger.info("Created the %s table (streams enabled).", table_name)
            return
        except client.exceptions.ResourceInUseException:
            # Already provisioned — a previous run, or this process restarting. Nothing to do.
            logger.info("The %s table already exists.", table_name)
            return
        except Exception as ex:  # noqa: BLE001 - warmup: any transient error is worth a retry
            if attempt >= _MAX_ATTEMPTS:
                raise
            logger.info(
                "Could not provision the %s table yet (attempt %d/%d) - retrying: %s",
                table_name,
                attempt,
                _MAX_ATTEMPTS,
                ex,
            )
            time.sleep(_RETRY_INTERVAL_SECONDS)
