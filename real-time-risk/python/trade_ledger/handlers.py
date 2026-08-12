"""The ``trade:book`` command handler — books a trade as an immutable ledger event.

Mirrors .NET ``TradeLedger/BookTradeHandler.cs``. The handler is a plain ``async def`` produced by a
factory that closes over its :class:`DynamoDbEventStore` dependency (the Pythonic take on .NET's
constructor injection, matching the ``make_*`` pattern in benzene-python's own ``orders_domain``
example). It carries both a ``@message`` topic tag and an ``@http_endpoint`` route tag, so the same
handler is reachable as ``POST /trades`` over HTTP *and* as the ``trade:book`` message topic — the
exact decorator idiom benzene-http documents.
"""

from __future__ import annotations

import uuid
from datetime import datetime, timezone

from benzene.core import Handler, message
from benzene.core.mapping import encode_body
from benzene.http import http_endpoint
from benzene.results import Result

from contracts import (
    BOOK_TRADE_TOPIC,
    TRADE_BOOKED_EVENT_TYPE,
    BookTradeRequest,
    BookTradeResponse,
    TradeBooked,
    TradeSide,
)

from .event_store import DynamoDbEventStore, EventEnvelope, EventStoreConcurrencyError


def make_book_trade(event_store: DynamoDbEventStore) -> Handler:
    """Build the ``trade:book`` / ``POST /trades`` handler over the given event store."""

    @http_endpoint("POST", "/trades")
    @message(BOOK_TRADE_TOPIC)
    async def book_trade(request: BookTradeRequest) -> Result:
        # Validation mirrors .NET: book & symbol non-empty, quantity > 0, price > 0. A
        # validation-error Result maps to HTTP 422 (benzene-http status table).
        if (
            not (request.book or "").strip()
            or not (request.symbol or "").strip()
            or request.quantity <= 0
            or request.price <= 0
        ):
            return Result.validation_error(
                "Book, Symbol, Quantity (> 0) and Price (> 0) are required."
            )

        # Stream id = book id. Read the stream's current head for optimistic concurrency — fine for
        # this demo's traffic; a high-contention book would instead have the caller supply the version
        # it last saw and retry on EventStoreConcurrencyError.
        stream_id = request.book
        history = await event_store.read(stream_id)
        expected_version = history[-1].version if history else 0

        trade = TradeBooked(
            trade_id=str(uuid.uuid4()),
            book=request.book,
            symbol=request.symbol,
            side=TradeSide(request.side),
            quantity=request.quantity,
            price=request.price,
            booked_at=datetime.now(timezone.utc).isoformat(),
        )

        # encode_body applies the shared wire naming policy (camelCase fields, "Buy"/"Sell" for the
        # side enum) to the event payload the stream will carry.
        payload = encode_body(trade)
        try:
            new_version = await event_store.append(
                stream_id,
                expected_version,
                [EventEnvelope(TRADE_BOOKED_EVENT_TYPE, payload)],
            )
        except EventStoreConcurrencyError as ex:
            # Another writer beat us to this version. Surface it as a conflict (HTTP 409).
            return Result.conflict(str(ex))

        return Result.ok(
            BookTradeResponse(trade_id=trade.trade_id, book=request.book, version=new_version)
        )

    return book_trade
