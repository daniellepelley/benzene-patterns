"""Shared message contracts for the Real-Time Risk & Trading Platform's Trade Ledger + Risk Read
Models slice — the Python port of ``real-time-risk/dotnet/Contracts``.

These are the request/response payload shapes and the event body the two services address each other
by, kept in one module so the Trade Ledger (producer) and the Risk Read Models (consumer) never drift
on a topic string, an event-type discriminator, or a field name.

**Wire naming.** Benzene's serializer (``benzene.core.mapping.to_jsonable``) emits **camelCase** for
dataclass field names automatically (wire-contracts §6): a Python field ``net_quantity`` is written
``netQuantity`` on the wire, and an inbound ``netQuantity`` / ``net_quantity`` / ``netquantity`` all
map back onto it case-insensitively (``to_request``). So the dataclasses below are written in idiomatic
snake_case, and the on-the-wire JSON is the exact camelCase the .NET service emits (``tradeId``,
``version``, ``netQuantity``, ``realizedCash``, ``projectedThroughVersion``, ``bookedAt``, …) — which
is what the shared black-box CI test asserts against. No pydantic aliases or custom encoder are needed;
plain dataclasses ride the framework's own naming policy.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum

# --- Topics -------------------------------------------------------------------------------------

#: Command: book a trade to the ledger. Handled by the Trade Ledger service.
BOOK_TRADE_TOPIC = "trade:book"

#: Query: current positions for a book. Handled by the Risk Read Models service.
BOOK_POSITIONS_TOPIC = "book:positions"

#: The event-type discriminator stored on every ledger event (the event store item's ``eventType``
#: attribute). One event type today; more (cash movements, fees) are future work per
#: ``real-time-risk/README.md``'s build order. Mirrors .NET ``Topics.TradeBookedEventType``.
TRADE_BOOKED_EVENT_TYPE = "TradeBooked"


class TradeSide(str, Enum):
    """Buy or Sell.

    A ``str`` subclass whose *values* are ``"Buy"`` / ``"Sell"`` so it serializes to those exact
    tokens on the wire (``json.dumps`` renders a ``str``-enum as its string value) and round-trips
    without a custom converter — the Python analogue of .NET's ``[JsonStringEnumConverter]`` on
    ``TradeSide``. Because it is a ``str``, ``TradeSide.BUY == "Buy"`` is ``True``, so comparisons hold
    whether a value arrived already coerced to the enum or as a bare string off the decoded payload.
    """

    BUY = "Buy"
    SELL = "Sell"


# --- trade:book (command) -----------------------------------------------------------------------


@dataclass
class BookTradeRequest:
    """The ``trade:book`` command request (``POST /trades``). Mirrors .NET ``BookTradeRequest``."""

    book: str = ""
    symbol: str = ""
    side: TradeSide = TradeSide.BUY
    quantity: float = 0.0
    price: float = 0.0


@dataclass
class BookTradeResponse:
    """The ``trade:book`` command response — the resulting ledger position.

    Mirrors .NET ``BookTradeResponse``. ``version`` is the book's ledger stream version after this
    trade was appended (a fresh book's first trade is version 1).
    """

    trade_id: str = ""
    book: str = ""
    version: int = 0


@dataclass
class TradeBooked:
    """The immutable event appended to the ledger for every booked trade (the JSON ``payload`` of a
    ``TradeBooked`` event store item) — the Trade Ledger's write side and, via the event table's
    DynamoDB Stream, the Risk Read Models' projection input. Mirrors .NET ``TradeBooked``.
    """

    trade_id: str
    book: str
    symbol: str
    side: TradeSide
    quantity: float
    price: float
    booked_at: str  # ISO-8601 UTC timestamp


# --- book:positions (query) ---------------------------------------------------------------------


@dataclass
class BookPositionsRequest:
    """The ``book:positions`` query request (``GET /books/{book}/positions``).

    Mirrors .NET ``BookPositionsRequest``. The ``{book}`` path segment binds onto ``book`` — the HTTP
    binding merges captured path parameters into the handler's request (see PARITY-NOTES.md §2).
    """

    book: str = ""


@dataclass
class PositionView:
    """One symbol's net position and realized cash flow within a book, as projected from the ledger.

    Mirrors .NET ``PositionView``. ``net_quantity`` — buys add, sells subtract. ``realized_cash`` —
    net cash flow from trading the symbol: a sell adds proceeds, a buy subtracts cost.
    """

    symbol: str = ""
    net_quantity: float = 0.0
    realized_cash: float = 0.0


@dataclass
class BookPositionsResponse:
    """The ``book:positions`` query response — one row per symbol traded in the book.

    Mirrors .NET ``BookPositionsResponse``. ``projected_through_version`` is the highest ledger event
    version this projection has consumed for the book, so a caller can tell whether a just-booked
    trade has been reflected yet (the CDC projection is eventually consistent, not read-your-writes).
    """

    book: str = ""
    positions: list[PositionView] = field(default_factory=list)
    projected_through_version: int = 0
