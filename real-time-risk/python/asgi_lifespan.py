"""A thin ASGI wrapper adding the lifespan protocol around a Benzene ASGI app.

:class:`benzene.http.BenzeneHttpApp` handles only ``http`` scopes — it deliberately rejects any other
scope type, including ``lifespan`` (the ASGI startup/shutdown channel). That is correct for the
framework, but this port needs *some* per-process lifecycle: the Trade Ledger must provision its table
before it serves, and the Risk Read Models service must start (and later stop) its DynamoDB-Streams
poller. In .NET those hang off ASP.NET Core's own lifecycle (``await ...EnsureTradesTableExistsAsync``
before ``app.Run()``; ``AddHostedService<TradeStreamProjector>``).

:class:`LifespanApp` supplies the Python equivalent: it answers the ``lifespan`` scope with the given
``on_startup`` / ``on_shutdown`` coroutines and delegates every ``http`` scope straight through to the
wrapped Benzene app. It adds lifecycle *around* Benzene dispatch; it never intercepts or reroutes a
request, so Benzene's routing/pipeline stays the sole request path.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from typing import Any

Hook = Callable[[], Awaitable[None]]


class LifespanApp:
    def __init__(
        self,
        app: Any,
        *,
        on_startup: Hook | None = None,
        on_shutdown: Hook | None = None,
    ) -> None:
        self._app = app
        self._on_startup = on_startup
        self._on_shutdown = on_shutdown

    async def __call__(self, scope: dict, receive: Any, send: Any) -> None:
        if scope.get("type") != "lifespan":
            await self._app(scope, receive, send)
            return

        while True:
            message = await receive()
            if message["type"] == "lifespan.startup":
                try:
                    if self._on_startup is not None:
                        await self._on_startup()
                except Exception as ex:  # noqa: BLE001 - report failure to the server, don't crash
                    await send({"type": "lifespan.startup.failed", "message": str(ex)})
                    return
                await send({"type": "lifespan.startup.complete"})
            elif message["type"] == "lifespan.shutdown":
                try:
                    if self._on_shutdown is not None:
                        await self._on_shutdown()
                finally:
                    await send({"type": "lifespan.shutdown.complete"})
                return
