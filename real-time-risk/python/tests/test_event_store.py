"""Unit tests for the app-local :class:`DynamoDbEventStore` against a recording fake boto3 client.

No docker / no real DynamoDB (the live integration is the CI smoke test's job). These tests pin the
*parity-critical* details the framework does not give us and we therefore own by hand: the exact item
shape written (``pk`` / ``version`` / ``eventType`` / ``payload`` / ``timestamp``), the 1-based
version sequencing, the ``attribute_not_exists(pk)`` optimistic-concurrency guard on every Put, and
the mapping of a cancelled transaction to :class:`EventStoreConcurrencyError`.
"""

from __future__ import annotations

import asyncio

import pytest
from botocore.exceptions import ClientError

from trade_ledger.event_store import (
    DynamoDbEventStore,
    EventEnvelope,
    EventStoreConcurrencyError,
)


class FakeDynamoClient:
    """Records boto3 calls and lets a test force a transaction cancellation."""

    def __init__(self, existing_items: list[dict] | None = None) -> None:
        self.query_calls: list[dict] = []
        self.transact_calls: list[list[dict]] = []
        self._items = existing_items or []
        self.cancel_next_transaction = False

    def query(self, **kwargs):
        self.query_calls.append(kwargs)
        return {"Items": self._items}

    def transact_write_items(self, TransactItems):  # noqa: N803 - boto3's kwarg name
        self.transact_calls.append(TransactItems)
        if self.cancel_next_transaction:
            raise ClientError(
                {"Error": {"Code": "TransactionCanceledException", "Message": "cancelled"}},
                "TransactWriteItems",
            )


def _run(coro):
    return asyncio.run(coro)


def test_append_writes_shared_item_shape_at_version_1() -> None:
    client = FakeDynamoClient()
    store = DynamoDbEventStore(client, "trades")

    new_version = _run(
        store.append("desk-a", 0, [EventEnvelope("TradeBooked", '{"symbol":"AAPL"}')])
    )

    assert new_version == 1
    put = client.transact_calls[0][0]["Put"]
    assert put["TableName"] == "trades"
    assert put["ConditionExpression"] == "attribute_not_exists(pk)"
    item = put["Item"]
    assert item["pk"] == {"S": "desk-a"}
    assert item["version"] == {"N": "1"}
    assert item["eventType"] == {"S": "TradeBooked"}
    assert item["payload"] == {"S": '{"symbol":"AAPL"}'}
    assert set(item["timestamp"]) == {"S"} and item["timestamp"]["S"]  # ISO-8601 string present


def test_append_sequences_versions_from_expected() -> None:
    client = FakeDynamoClient()
    store = DynamoDbEventStore(client, "trades")

    new_version = _run(
        store.append(
            "desk-a",
            5,
            [EventEnvelope("TradeBooked", "{}"), EventEnvelope("TradeBooked", "{}")],
        )
    )

    assert new_version == 7
    versions = [i["Put"]["Item"]["version"]["N"] for i in client.transact_calls[0]]
    assert versions == ["6", "7"]


def test_cancelled_transaction_raises_concurrency_error() -> None:
    client = FakeDynamoClient()
    client.cancel_next_transaction = True
    store = DynamoDbEventStore(client, "trades")

    with pytest.raises(EventStoreConcurrencyError):
        _run(store.append("desk-a", 0, [EventEnvelope("TradeBooked", "{}")]))


def test_read_queries_ascending_and_maps_items() -> None:
    items = [
        {
            "pk": {"S": "desk-a"},
            "version": {"N": "1"},
            "eventType": {"S": "TradeBooked"},
            "payload": {"S": '{"symbol":"AAPL"}'},
            "timestamp": {"S": "2026-08-12T00:00:00+00:00"},
        }
    ]
    client = FakeDynamoClient(existing_items=items)
    store = DynamoDbEventStore(client, "trades")

    history = _run(store.read("desk-a"))

    assert client.query_calls[0]["ScanIndexForward"] is True
    assert client.query_calls[0]["KeyConditionExpression"] == "pk = :pk"
    assert client.query_calls[0]["ExpressionAttributeValues"] == {":pk": {"S": "desk-a"}}
    assert len(history) == 1
    assert history[0].version == 1
    assert history[0].event_type == "TradeBooked"
    assert history[0].payload == '{"symbol":"AAPL"}'


def test_empty_append_is_noop() -> None:
    client = FakeDynamoClient()
    store = DynamoDbEventStore(client, "trades")
    assert _run(store.append("desk-a", 3, [])) == 3
    assert client.transact_calls == []
