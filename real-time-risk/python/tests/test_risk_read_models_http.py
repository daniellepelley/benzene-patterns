"""HTTP-front-door tests for the Risk Read Models query handler.

The point of this file is the **path-parameter binding** question the parity audit asked (PARITY-NOTES
§2): does ``@http_endpoint("GET", "/books/{book}/positions")`` deliver the ``{book}`` segment to the
handler's request? These tests boot the real :class:`RiskReadModelsStartUp` and drive it through
benzene-http's actual ASGI binding, proving the captured ``book`` reaches ``BookPositionsRequest.book``
and selects the right projection — no ASGI-scope reach-around needed.
"""

from __future__ import annotations

import json

from benzene.testing import create_test_host

from contracts import TradeBooked, TradeSide
from risk_read_models.startup import RiskReadModelsStartUp
from risk_read_models.store import BookPositionsStore


def make_host(store: BookPositionsStore):
    def overrides(services):
        services.add_instance(BookPositionsStore, store)

    return create_test_host(RiskReadModelsStartUp).with_services(overrides).build_http()


def test_path_param_book_binds_and_selects_projection() -> None:
    store = BookPositionsStore()
    store.apply(
        TradeBooked("t", "desk-a", "AAPL", TradeSide.BUY, 100, 150.25, "2026-08-12T00:00:00Z"),
        version=1,
    )
    host = make_host(store)

    response = host.send_http("GET", "/books/desk-a/positions")

    assert response.status_code == 200
    body = json.loads(response.body)
    assert body["book"] == "desk-a"  # the {book} path segment bound onto the request
    assert body["projectedThroughVersion"] == 1
    assert body["positions"][0]["symbol"] == "AAPL"
    assert body["positions"][0]["netQuantity"] == 100


def test_unknown_book_is_empty_ok() -> None:
    host = make_host(BookPositionsStore())
    response = host.send_http("GET", "/books/never-traded/positions")
    assert response.status_code == 200
    body = json.loads(response.body)
    assert body["book"] == "never-traded"
    assert body["positions"] == []
    assert body["projectedThroughVersion"] == 0
