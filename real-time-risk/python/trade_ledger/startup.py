"""The Trade Ledger composition root — the ``BenzeneStartUp`` every host and test boots from.

Mirrors .NET ``TradeLedger/StartUp.cs``. Registers the app-local :class:`DynamoDbEventStore` (the
event-sourcing package benzene-python doesn't ship — see `event_store.py` / PARITY-NOTES.md) and wires
the single ``trade:book`` / ``POST /trades`` handler. Deployment and tests build from this same
startup; a test overrides the ``DynamoDbEventStore`` registration with an in-memory fake, nothing else
changes.
"""

from __future__ import annotations

from collections.abc import Mapping

from benzene.core import AppDefinition, BenzeneStartUp, Container, Scope
from benzene.http import HttpRouter

from .dynamodb import create_dynamodb_client, trades_table_name
from .event_store import DynamoDbEventStore
from .handlers import make_book_trade


class TradeLedgerStartUp(BenzeneStartUp):
    """Registers the event store and wires the ``POST /trades`` route + ``trade:book`` topic."""

    def configure_services(self, services: Container, config: Mapping[str, str]) -> None:
        services.try_add_singleton(
            DynamoDbEventStore,
            lambda _scope: DynamoDbEventStore(create_dynamodb_client(), trades_table_name()),
        )

    def configure(self, services: Scope, config: Mapping[str, str]) -> AppDefinition:
        event_store = services.get_service(DynamoDbEventStore)
        router = HttpRouter().add(make_book_trade(event_store))
        return AppDefinition(router=router)
