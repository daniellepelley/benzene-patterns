"""Unit tests for the Risk Read Models projection fold (`BookPositionsStore`).

Covers the position/cash math for buys and sells, idempotency by (book, version) (DynamoDB Streams is
at-least-once and the poller re-reads from TRIM_HORIZON on restart), symbol ordering, and the empty
result for an unknown book — the exact behaviours mirrored from .NET ``BookPositionsStore``. No docker,
no boto3: the fold is pure in-memory code.

Also asserts the **camelCase wire shape** the shared black-box CI test depends on, by pushing the
query response through Benzene's own serializer (`to_jsonable`) and checking the emitted keys.
"""

from __future__ import annotations

from benzene.core.mapping import to_jsonable

from contracts import TradeBooked, TradeSide
from risk_read_models.store import BookPositionsStore


def _trade(book: str, symbol: str, side: TradeSide, quantity: float, price: float) -> TradeBooked:
    return TradeBooked(
        trade_id="t-1",
        book=book,
        symbol=symbol,
        side=side,
        quantity=quantity,
        price=price,
        booked_at="2026-08-12T00:00:00+00:00",
    )


def test_buy_adds_position_and_spends_cash() -> None:
    store = BookPositionsStore()
    store.apply(_trade("desk-a", "AAPL", TradeSide.BUY, 100, 150.25), version=1)

    result = store.query("desk-a")
    assert result.projected_through_version == 1
    assert len(result.positions) == 1
    position = result.positions[0]
    assert position.symbol == "AAPL"
    assert position.net_quantity == 100
    # A buy subtracts cost: -100 * 150.25.
    assert position.realized_cash == -15025.0


def test_sell_subtracts_position_and_brings_cash_in() -> None:
    store = BookPositionsStore()
    store.apply(_trade("desk-a", "AAPL", TradeSide.SELL, 40, 10.0), version=1)

    position = store.query("desk-a").positions[0]
    assert position.net_quantity == -40
    # A sell adds proceeds: +40 * 10.
    assert position.realized_cash == 400.0


def test_buy_then_sell_nets_position_and_cash() -> None:
    store = BookPositionsStore()
    store.apply(_trade("desk-a", "AAPL", TradeSide.BUY, 100, 150.0), version=1)
    store.apply(_trade("desk-a", "AAPL", TradeSide.SELL, 30, 160.0), version=2)

    result = store.query("desk-a")
    assert result.projected_through_version == 2
    position = result.positions[0]
    assert position.net_quantity == 70  # 100 - 30
    assert position.realized_cash == -15000.0 + 4800.0  # -(100*150) + (30*160)


def test_apply_is_idempotent_by_version() -> None:
    store = BookPositionsStore()
    trade = _trade("desk-a", "AAPL", TradeSide.BUY, 100, 150.0)

    store.apply(trade, version=1)
    store.apply(trade, version=1)  # redelivery of the same version must not double-apply

    result = store.query("desk-a")
    assert result.positions[0].net_quantity == 100
    assert result.projected_through_version == 1


def test_bare_string_side_matches_enum() -> None:
    # The projector builds TradeBooked via to_request, which leaves `side` as a bare "Buy"/"Sell"
    # string; the fold must treat it the same as the enum member.
    store = BookPositionsStore()
    trade = _trade("desk-a", "AAPL", TradeSide.BUY, 100, 150.0)
    object.__setattr__(trade, "side", "Buy")  # simulate the decoded, un-coerced value
    store.apply(trade, version=1)
    assert store.query("desk-a").positions[0].net_quantity == 100


def test_positions_sorted_by_symbol() -> None:
    store = BookPositionsStore()
    store.apply(_trade("desk-a", "MSFT", TradeSide.BUY, 1, 1.0), version=1)
    store.apply(_trade("desk-a", "AAPL", TradeSide.BUY, 1, 1.0), version=2)
    store.apply(_trade("desk-a", "GOOG", TradeSide.BUY, 1, 1.0), version=3)

    symbols = [p.symbol for p in store.query("desk-a").positions]
    assert symbols == ["AAPL", "GOOG", "MSFT"]


def test_unknown_book_is_empty() -> None:
    store = BookPositionsStore()
    result = store.query("never-traded")
    assert result.book == "never-traded"
    assert result.positions == []
    assert result.projected_through_version == 0


def test_query_response_serializes_camelcase() -> None:
    store = BookPositionsStore()
    store.apply(_trade("desk-a", "AAPL", TradeSide.BUY, 100, 150.25), version=1)

    wire = to_jsonable(store.query("desk-a"))
    assert set(wire) == {"book", "positions", "projectedThroughVersion"}
    assert wire["projectedThroughVersion"] == 1
    assert set(wire["positions"][0]) == {"symbol", "netQuantity", "realizedCash"}
    assert wire["positions"][0]["netQuantity"] == 100
