"""The ``book:positions`` query handler — serves a book's positions from the projection.

Mirrors .NET ``RiskReadModels/BookPositionsQueryHandler.cs``. Built by a factory closing over the
:class:`BookPositionsStore`, and tagged ``@message`` + ``@http_endpoint`` so the same handler answers
``GET /books/{book}/positions`` and the ``book:positions`` topic.

**Path-param binding (the audited question).** The ``{book}`` segment binds straight onto the request:
benzene-http's binding merges captured path parameters into the handler's request object (path wins
over query wins over body), and ``to_request`` maps them case-insensitively into
:class:`BookPositionsRequest`. So a plain ``request.book`` is populated — no ASGI-scope reach-around or
custom route needed. See PARITY-NOTES.md §2 for the evidence.
"""

from __future__ import annotations

from benzene.core import Handler, message
from benzene.http import http_endpoint
from benzene.results import Result

from contracts import BOOK_POSITIONS_TOPIC, BookPositionsRequest

from .store import BookPositionsStore


def make_book_positions(store: BookPositionsStore) -> Handler:
    """Build the ``book:positions`` / ``GET /books/{book}/positions`` handler over the store."""

    @http_endpoint("GET", "/books/{book}/positions")
    @message(BOOK_POSITIONS_TOPIC)
    async def book_positions(request: BookPositionsRequest) -> Result:
        if not (request.book or "").strip():
            return Result.validation_error("Book is required.")
        return Result.ok(store.query(request.book))

    return book_positions
