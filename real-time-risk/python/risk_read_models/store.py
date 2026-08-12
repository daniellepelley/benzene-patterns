"""The read-model projection — per book, per symbol net position + realized cash.

Mirrors .NET ``RiskReadModels/BookPositionsStore.cs``. Folded from :class:`TradeBooked` events as the
:class:`TradeStreamProjector` consumes them off the ledger's DynamoDB Stream. In-memory only for this
first slice (it resets on restart/redeploy) — a real store isn't needed to prove the pattern yet.

**Idempotent by (book, version).** DynamoDB Streams is at-least-once, and the local poller re-reads
from ``TRIM_HORIZON`` on every restart, so a redelivered version must not be double-applied. Each
book tracks the set of versions it has already folded and ignores repeats. A ``threading.Lock`` guards
mutation because the projector runs on its own thread (``asyncio.to_thread``) while HTTP queries read
concurrently on the event loop.
"""

from __future__ import annotations

import threading

from contracts import BookPositionsResponse, PositionView, TradeBooked, TradeSide


class _Book:
    __slots__ = ("positions", "applied_versions", "projected_through_version")

    def __init__(self) -> None:
        self.positions: dict[str, PositionView] = {}
        self.applied_versions: set[int] = set()
        self.projected_through_version: int = 0


class BookPositionsStore:
    """The queryable per-book projection built from the ledger's events."""

    def __init__(self) -> None:
        self._books: dict[str, _Book] = {}
        self._gate = threading.Lock()

    def apply(self, trade: TradeBooked, version: int) -> None:
        """Fold one :class:`TradeBooked` into its book's projection. Idempotent by (book, version)."""
        with self._gate:
            book = self._books.setdefault(trade.book, _Book())
            if version in book.applied_versions:
                return
            book.applied_versions.add(version)

            current = book.positions.get(trade.symbol, PositionView(symbol=trade.symbol))
            # Buy adds to the net position and spends cash; sell subtracts and brings cash in.
            signed_quantity = (
                trade.quantity if trade.side == TradeSide.BUY else -trade.quantity
            )
            cash_flow = (
                trade.quantity * trade.price
                if trade.side == TradeSide.SELL
                else -(trade.quantity * trade.price)
            )
            book.positions[trade.symbol] = PositionView(
                symbol=trade.symbol,
                net_quantity=current.net_quantity + signed_quantity,
                realized_cash=current.realized_cash + cash_flow,
            )

            if version > book.projected_through_version:
                book.projected_through_version = version

    def query(self, book_id: str) -> BookPositionsResponse:
        """Read a book's current projection; an unknown book returns an empty (zero-trade) result."""
        with self._gate:
            book = self._books.get(book_id)
            if book is None:
                return BookPositionsResponse(book=book_id, positions=[], projected_through_version=0)
            return BookPositionsResponse(
                book=book_id,
                positions=[book.positions[s] for s in sorted(book.positions)],
                projected_through_version=book.projected_through_version,
            )
