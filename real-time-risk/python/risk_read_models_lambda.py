"""AWS Lambda entry point for the Risk Read Models service.

    handler = risk_read_models_lambda.handler   # the callable Lambda invokes

One Lambda function hosting BOTH sides of the CQRS read model, exactly as the local ASGI host
(`risk_read_models/main.py`) does in one process — only the host changes from uvicorn + a background
poller to :class:`~benzene.aws.AwsLambdaApp`:

- **the DynamoDB-Streams projection** (the event-source-mapping trigger): fold each ``TradeBooked``
  INSERT off the ledger table's stream into the in-memory :class:`~risk_read_models.store.BookPositionsStore`, and
- **the query** (the API Gateway trigger): serve ``GET /books/{book}/positions`` from that same store.

``AwsLambdaApp`` already multiplexes trigger types in one handler (it dispatches on the event shape —
API Gateway vs. DynamoDB stream vs. SQS …), so, unlike the Go port, no hand-written multiplexer is
needed here: this module just registers both handlers on one registry and hands it to the host.

**Wiring the two handlers onto one store.** :func:`~benzene.core.build_application` boots the SAME
:class:`~risk_read_models.startup.RiskReadModelsStartUp` the ASGI host uses — that registers the
singleton ``BookPositionsStore`` and the ``GET /books/{book}/positions`` query handler (which closes
over the store) — and returns the root scope so we can resolve that *same* store singleton and close
the stream-projection handler over it too. Both handlers therefore fold into / read from one store,
just as the local poller and query share one store instance.

**Stream topic.** benzene-python's DynamoDB-stream binding maps each record to a Benzene envelope
whose topic is a configured convention. We pin it to ``f"{table}:INSERT"`` (via ``dynamodb_topic``)
to match the Go port and the cross-port ``"{table}:{eventName}"`` convention, and register the
projection handler on exactly that topic. The ledger is append-only, so INSERT is the only change
type a record ever arrives as (MODIFY/REMOVE never happen — which is also why the shared Terraform
filters the event-source mapping to INSERT); a hypothetical non-INSERT record would route to this
same pinned topic and be handled identically, which is harmless here.

**Honest limitation — in-memory read model vs. Lambda scaling.** A single warm instance both projects
and serves into one process-local store, so within an instance a query sees what that instance
projected. But Lambda scales to MANY instances, each with its OWN empty store, and AWS routes stream
shards and API requests to whichever instance it likes — so a query can hit an instance that never
projected the record and return stale/empty data, and a cold start begins empty. Fine for the local
one-process Compose slice and for proving the hosting shape, but NOT a correct production read model:
the production answer is a shared store (project into DynamoDB/ElastiCache; the query reads it). Out
of scope here. Documented in PARITY-NOTES.md §7.
"""

from __future__ import annotations

import json
from typing import Any

from benzene.aws import AwsLambdaApp, to_lambda_handler
from benzene.core import build_application
from benzene.core.mapping import to_request
from benzene.core.registry import Registry
from benzene.results import Result

from contracts import TRADE_BOOKED_EVENT_TYPE, TradeBooked
from risk_read_models.startup import RiskReadModelsStartUp
from risk_read_models.store import BookPositionsStore
from trade_ledger.dynamodb import trades_table_name


def _make_projection(store: BookPositionsStore):
    """Build the DynamoDB-stream projection handler over ``store``.

    The body benzene-python's stream binding delivers is the record's ``dynamodb`` projection
    (``Keys`` / ``NewImage`` / ``OldImage`` in DynamoDB's *attribute-value* encoding, e.g.
    ``{"version": {"N": "1"}, "payload": {"S": "..."}}``) — unlike the Go binding, it is NOT decoded
    to plain JSON, so this handler decodes the ``NewImage`` itself. That decode mirrors
    :meth:`risk_read_models.projector.TradeStreamProjector._apply` exactly (the topic already encodes
    INSERT, so there is no ``eventName`` to re-check here); the projection math is reused verbatim via
    :meth:`BookPositionsStore.apply`.
    """

    async def project(record: dict[str, Any]) -> Result:
        image = record.get("NewImage")
        if not image:
            return Result.ok()  # a record with no new image (not an append) — nothing to fold.
        if image.get("eventType", {}).get("S") != TRADE_BOOKED_EVENT_TYPE:
            return Result.ok()  # not a TradeBooked event — ignore (there are none today).
        payload = image.get("payload", {}).get("S")
        version_raw = image.get("version", {}).get("N")
        if not payload or version_raw is None:
            return Result.ok()  # malformed item shape — skip rather than fail the whole batch.

        version = int(version_raw)
        try:
            trade = to_request(TradeBooked, json.loads(payload))
        except (ValueError, TypeError) as ex:
            # A record whose payload cannot be parsed is reported as a failure so the event-source
            # mapping redelivers it (batchItemFailures), rather than silently dropping a ledger event.
            return Result.bad_request(
                f"unparseable TradeBooked payload at version {version}: {ex}"
            )

        store.apply(trade, version)
        return Result.ok()

    return project


def _build_handler():
    """Compose the one-function read model: query handler + stream projection on one store."""
    # Boot the same composition root the ASGI host uses; keep the root scope to resolve the store.
    definition, scope = build_application(RiskReadModelsStartUp)
    store = scope.get_service(BookPositionsStore)

    table = trades_table_name()
    stream_topic = f"{table}:INSERT"

    # The registry the Lambda host dispatches through: the router's GET /books/{book}/positions
    # handler PLUS the stream-projection handler, both over the same store.
    registry = Registry.from_definitions(definition.router).register(
        stream_topic, _make_projection(store)
    )

    app = AwsLambdaApp(
        http_router=definition.router,
        registry=registry,
        dynamodb_topic=stream_topic,
    )
    return to_lambda_handler(app)


#: The ``handler(event, context)`` callable AWS Lambda invokes (its configured handler is
#: ``risk_read_models_lambda.handler``). Built once per cold start.
handler = _build_handler()
