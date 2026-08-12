"""Unit tests for the Trade Ledger `trade:book` handler, driven through the real HTTP front door.

Boots the *real* :class:`TradeLedgerStartUp` via benzene-testing's ``create_test_host`` and overrides
only the external edge — the DynamoDB event store — with an in-memory fake honoring the same
``read`` / ``append`` async contract. This dogfoods the actual pipeline, routing, request mapping, and
response serialization (the exact setup benzene-python's own example suites use), while the DynamoDB
integration itself is covered by the CI smoke test (no docker daemon here).

Asserts the happy path (first trade on a fresh book is version 1, camelCase body), validation
(HTTP 422), and optimistic-concurrency conflict handling (HTTP 409).
"""

from __future__ import annotations

import json

import pytest

from benzene.testing import create_test_host

from trade_ledger.event_store import (
    DynamoDbEventStore,
    EventEnvelope,
    EventStoreConcurrencyError,
    StoredEvent,
)
from trade_ledger.startup import TradeLedgerStartUp


class FakeEventStore:
    """In-memory stand-in for :class:`DynamoDbEventStore` (same async read/append surface)."""

    def __init__(self) -> None:
        self.streams: dict[str, list[StoredEvent]] = {}
        self.fail_next_append_with_conflict = False

    async def read(self, stream_id: str) -> list[StoredEvent]:
        return list(self.streams.get(stream_id, []))

    async def append(
        self, stream_id: str, expected_version: int, events: list[EventEnvelope]
    ) -> int:
        if self.fail_next_append_with_conflict:
            raise EventStoreConcurrencyError("simulated concurrent append")
        stream = self.streams.setdefault(stream_id, [])
        if (stream[-1].version if stream else 0) != expected_version:
            raise EventStoreConcurrencyError("expected version mismatch")
        version = expected_version
        for envelope in events:
            version += 1
            stream.append(
                StoredEvent(version, envelope.event_type, envelope.payload, "2026-08-12T00:00:00Z")
            )
        return version


def make_host(store: FakeEventStore):
    def overrides(services):
        services.add_instance(DynamoDbEventStore, store)

    return create_test_host(TradeLedgerStartUp).with_services(overrides).build_http()


def test_first_trade_on_fresh_book_is_version_1() -> None:
    store = FakeEventStore()
    host = make_host(store)

    response = host.send_http(
        "POST",
        "/trades",
        body={"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": 100, "price": 150.25},
    )

    assert response.status_code == 200
    body = json.loads(response.body)
    assert body["version"] == 1
    assert body["book"] == "desk-a"
    assert body["tradeId"]  # a uuid was minted and returned as camelCase `tradeId`
    # The event landed in the stream with the shared item-shape event type.
    assert store.streams["desk-a"][0].event_type == "TradeBooked"


def test_second_trade_increments_version() -> None:
    store = FakeEventStore()
    host = make_host(store)
    common = {"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": 10, "price": 1.0}

    first = json.loads(host.send_http("POST", "/trades", body=common).body)
    second = json.loads(host.send_http("POST", "/trades", body=common).body)

    assert first["version"] == 1
    assert second["version"] == 2


@pytest.mark.parametrize(
    "body",
    [
        {"book": "", "symbol": "AAPL", "side": "Buy", "quantity": 10, "price": 1.0},
        {"book": "desk-a", "symbol": "", "side": "Buy", "quantity": 10, "price": 1.0},
        {"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": 0, "price": 1.0},
        {"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": 10, "price": 0},
        {"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": -5, "price": 1.0},
    ],
)
def test_invalid_trade_is_rejected(body: dict) -> None:
    store = FakeEventStore()
    host = make_host(store)
    response = host.send_http("POST", "/trades", body=body)
    assert response.status_code == 422  # validation-error -> 422
    assert store.streams == {}  # nothing appended on a rejected request


def test_concurrent_append_is_conflict() -> None:
    store = FakeEventStore()
    store.fail_next_append_with_conflict = True
    host = make_host(store)

    response = host.send_http(
        "POST",
        "/trades",
        body={"book": "desk-a", "symbol": "AAPL", "side": "Buy", "quantity": 10, "price": 1.0},
    )
    assert response.status_code == 409  # conflict


def test_sell_side_round_trips_through_payload() -> None:
    store = FakeEventStore()
    host = make_host(store)
    host.send_http(
        "POST",
        "/trades",
        body={"book": "desk-a", "symbol": "AAPL", "side": "Sell", "quantity": 5, "price": 2.0},
    )
    payload = json.loads(store.streams["desk-a"][0].payload)
    assert payload["side"] == "Sell"  # enum serialized as its "Sell" token
    assert payload["symbol"] == "AAPL"
    assert payload["quantity"] == 5
