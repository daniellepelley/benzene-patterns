"""The Risk Read Models composition root — the ``BenzeneStartUp`` every host and test boots from.

Mirrors .NET ``RiskReadModels/StartUp.cs``. Registers the in-memory :class:`BookPositionsStore` and
wires the single ``book:positions`` / ``GET /books/{book}/positions`` query handler. The
:class:`TradeStreamProjector` that feeds the store is a background poller started by the host
(`main.py`), not a Benzene-routed handler — it never receives an inbound request, it just runs
alongside the HTTP server (the local substitute for a Lambda DynamoDB-Streams trigger).
"""

from __future__ import annotations

from collections.abc import Mapping

from benzene.core import AppDefinition, BenzeneStartUp, Container, Scope
from benzene.http import HttpRouter

from .handlers import make_book_positions
from .store import BookPositionsStore


class RiskReadModelsStartUp(BenzeneStartUp):
    """Registers the projection store and wires the ``GET /books/{book}/positions`` route."""

    def configure_services(self, services: Container, config: Mapping[str, str]) -> None:
        services.try_add_singleton(BookPositionsStore)

    def configure(self, services: Scope, config: Mapping[str, str]) -> AppDefinition:
        store = services.get_service(BookPositionsStore)
        router = HttpRouter().add(make_book_positions(store))
        return AppDefinition(router=router)
