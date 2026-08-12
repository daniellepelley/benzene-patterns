"""An **app-local** DynamoDB-backed event store — the single biggest parity gap this port confirms.

**Why this lives in the app, not the framework.** .NET ships ``Benzene.EventSourcing`` /
``Benzene.EventSourcing.DynamoDb`` — an ``IEventStore`` with optimistic-concurrency append and a
DynamoDB store the Trade Ledger simply *inherits*. benzene-python has **no event-sourcing package at
all** (confirmed in ``real-time-risk/PARITY-FINDINGS.md`` §3.1 and its capability matrix §1: "Event
sourcing / DynamoDB-backed event store — ✅ .NET, ❌ Python"). So the Trade Ledger has to own the
~100 lines of "conditional-write append + query read" itself, re-implemented against the DynamoDB SDK,
exactly the per-language duplication the parity audit calls out. This class is that implementation.

**Wire/item-shape parity.** The item shape is byte-for-byte the shape .NET's ``DynamoDbEventStore``
writes and the shared Terraform table (`real-time-risk/deploy/terraform/dynamodb.tf`) provisions, so a
DynamoDB Stream consumer (the Risk Read Models projector) sees identical records regardless of which
language wrote them:

======================  ====  ==============================================================
attribute               type  meaning
======================  ====  ==============================================================
``pk``                  S     stream id (the book id) — the partition key
``version``             N     1-based per-stream sequence number — the sort key
``eventType``           S     the event discriminator (e.g. ``TradeBooked``)
``payload``             S     the event body as a JSON string
``timestamp``           S     ISO-8601 UTC instant the event was appended
======================  ====  ==============================================================

**Optimistic concurrency.** ``append`` issues a single ``TransactWriteItems`` with one ``Put`` per new
event, each guarded by ``ConditionExpression="attribute_not_exists(pk)"``. On a composite-key table
that condition is evaluated against the *exact* ``(pk, version)`` item being written, so it succeeds
only when that version slot is still free — precisely .NET's ``attribute_not_exists(#pk)`` guard. If
any Put's condition fails (a concurrent writer already took the version) DynamoDB cancels the whole
transaction; we surface that as :class:`EventStoreConcurrencyError`.

``boto3`` is synchronous, so each SDK call is dispatched through ``asyncio.to_thread`` to keep the
handler's event loop free — the store's public surface is ``async`` to match the handler contract.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any


@dataclass(frozen=True)
class EventEnvelope:
    """An event to append: its type discriminator and its already-serialized JSON payload.

    Mirrors .NET ``Benzene.EventSourcing.EventEnvelope(EventType, Payload)``.
    """

    event_type: str
    payload: str


@dataclass(frozen=True)
class StoredEvent:
    """An event read back from a stream: envelope fields plus the version it landed at.

    Mirrors .NET's ``StoredEvent`` (``EventType`` / ``Payload`` / ``Version`` / ``Timestamp``).
    """

    version: int
    event_type: str
    payload: str
    timestamp: str


class EventStoreConcurrencyError(Exception):
    """Raised when an :meth:`DynamoDbEventStore.append` lost an optimistic-concurrency race.

    Mirrors .NET ``EventStoreConcurrencyException``: the expected version no longer matches the
    stream's head, so the caller must re-read and retry.
    """


class DynamoDbEventStore:
    """A per-stream, append-only event log stored one item per event in a DynamoDB table."""

    def __init__(self, client: Any, table_name: str) -> None:
        self._client = client
        self._table_name = table_name

    async def read(self, stream_id: str) -> list[StoredEvent]:
        """Read a stream's full history in version order (``Query pk=:pk`` ascending).

        Mirrors .NET ``DynamoDbEventStore.ReadAsync``. An unknown stream returns ``[]``.
        """
        return await asyncio.to_thread(self._read_sync, stream_id)

    def _read_sync(self, stream_id: str) -> list[StoredEvent]:
        events: list[StoredEvent] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self._table_name,
                "KeyConditionExpression": "pk = :pk",
                "ExpressionAttributeValues": {":pk": {"S": stream_id}},
                "ScanIndexForward": True,  # ascending by version (the range key)
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self._client.query(**kwargs)
            for item in response.get("Items", []):
                events.append(
                    StoredEvent(
                        version=int(item["version"]["N"]),
                        event_type=item.get("eventType", {}).get("S", ""),
                        payload=item.get("payload", {}).get("S", ""),
                        timestamp=item.get("timestamp", {}).get("S", ""),
                    )
                )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        return events

    async def append(
        self, stream_id: str, expected_version: int, events: list[EventEnvelope]
    ) -> int:
        """Append events after ``expected_version``; return the stream's new highest version.

        Mirrors .NET ``DynamoDbEventStore.AppendAsync``. Each event is written at
        ``expected_version + n`` (1-based) inside one ``TransactWriteItems``, every Put guarded by
        ``attribute_not_exists(pk)`` so a concurrent writer that already claimed a version cancels the
        transaction — surfaced as :class:`EventStoreConcurrencyError`.
        """
        if not events:
            return expected_version
        return await asyncio.to_thread(self._append_sync, stream_id, expected_version, events)

    def _append_sync(
        self, stream_id: str, expected_version: int, events: list[EventEnvelope]
    ) -> int:
        from botocore.exceptions import ClientError

        now = datetime.now(timezone.utc).isoformat()
        transact_items = []
        new_version = expected_version
        for envelope in events:
            new_version += 1
            transact_items.append(
                {
                    "Put": {
                        "TableName": self._table_name,
                        "Item": {
                            "pk": {"S": stream_id},
                            "version": {"N": str(new_version)},
                            "eventType": {"S": envelope.event_type},
                            "payload": {"S": envelope.payload},
                            "timestamp": {"S": now},
                        },
                        # Optimistic concurrency: this (pk, version) slot must still be free.
                        "ConditionExpression": "attribute_not_exists(pk)",
                    }
                }
            )

        try:
            self._client.transact_write_items(TransactItems=transact_items)
        except ClientError as ex:
            code = ex.response.get("Error", {}).get("Code", "")
            # A cancelled transaction (a version slot was taken) is a lost concurrency race, not an
            # infrastructure fault. TransactionCanceledException is the batch-level code; the
            # per-item ConditionalCheckFailed reasons are inside CancellationReasons.
            if code in ("TransactionCanceledException", "ConditionalCheckFailedException"):
                raise EventStoreConcurrencyError(
                    f"Concurrent append to stream {stream_id!r} at expected version "
                    f"{expected_version} (a newer version already exists)."
                ) from ex
            raise
        return new_version
