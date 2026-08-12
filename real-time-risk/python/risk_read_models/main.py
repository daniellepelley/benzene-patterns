"""ASGI entry point for the Risk Read Models service (the uvicorn target).

    uvicorn risk_read_models.main:app --host 0.0.0.0 --port 8080

Boots :class:`RiskReadModelsStartUp` onto benzene-http's ASGI binding and starts the
:class:`TradeStreamProjector` as a background asyncio task on startup (cancelled on shutdown) — the
Python analogue of .NET ``RiskReadModels/Program.cs``'s ``AddHostedService<TradeStreamProjector>``.
The projector and the query handler share the *same* :class:`BookPositionsStore` singleton: the store
is resolved from the app's scope so the poller writes exactly what ``GET /books/{book}/positions``
reads.
"""

from __future__ import annotations

import asyncio

from benzene.core import build_application
from benzene.http import BenzeneHttpApp

from asgi_lifespan import LifespanApp
from trade_ledger.dynamodb import (
    create_dynamodb_client,
    create_streams_client,
    trades_table_name,
)

from .projector import TradeStreamProjector
from .startup import RiskReadModelsStartUp
from .store import BookPositionsStore


def _build_app() -> LifespanApp:
    definition, scope = build_application(RiskReadModelsStartUp)
    http_app = BenzeneHttpApp.from_definition(definition)
    store = scope.get_service(BookPositionsStore)

    projector = TradeStreamProjector(
        create_dynamodb_client(),
        create_streams_client(),
        store,
        trades_table_name(),
    )
    stop = asyncio.Event()
    task: asyncio.Task[None] | None = None

    async def on_startup() -> None:
        nonlocal task
        task = asyncio.create_task(projector.run(stop))

    async def on_shutdown() -> None:
        stop.set()
        if task is not None:
            await task

    return LifespanApp(http_app, on_startup=on_startup, on_shutdown=on_shutdown)


app = _build_app()
