"""ASGI entry point for the Trade Ledger service (the uvicorn target).

    uvicorn trade_ledger.main:app --host 0.0.0.0 --port 8080

Boots :class:`TradeLedgerStartUp` onto benzene-http's ASGI binding and, because this service *owns*
the ledger table, provisions it (idempotently, with retries) at startup before accepting requests —
the Python analogue of .NET ``TradeLedger/Program.cs`` (``EnsureTradesTableExistsAsync`` before
``app.Run()``). Provisioning runs on a worker thread so it doesn't block the event loop while it
retries through the DynamoDB-Local warmup window.
"""

from __future__ import annotations

import asyncio

from benzene.core import build_application
from benzene.http import BenzeneHttpApp

from asgi_lifespan import LifespanApp

from .dynamodb import create_dynamodb_client, trades_table_name
from .provisioning import ensure_trades_table_exists
from .startup import TradeLedgerStartUp


def _build_app() -> LifespanApp:
    definition, _ = build_application(TradeLedgerStartUp)
    http_app = BenzeneHttpApp.from_definition(definition)

    async def on_startup() -> None:
        await asyncio.to_thread(
            ensure_trades_table_exists, create_dynamodb_client(), trades_table_name()
        )

    return LifespanApp(http_app, on_startup=on_startup)


app = _build_app()
