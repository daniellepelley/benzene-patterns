"""Consumes the Trade Ledger's DynamoDB Stream and folds each ``TradeBooked`` into the projection.

Mirrors .NET ``RiskReadModels/TradeStreamProjector.cs``. In production this is exactly the job an AWS
Lambda DynamoDB-Streams event-source mapping does: benzene-python already ships that binding —
``AwsLambdaApp`` routes stream records via ``dynamodb_record_envelope`` to a ``{table}:{eventName}``
(default ``dynamodb:insert``) topic, and ``to_lambda_handler`` produces the Lambda entry point (see
``benzene/aws/app.py`` + ``events.py``). This local/Docker Compose slice **polls the same stream
directly** with boto3 instead of running inside a real Lambda, because emulating Lambda + a
Streams event-source mapping locally (LocalStack, Docker-in-Docker) adds a lot of moving parts for
little fidelity gain over polling — the same trade-off .NET's projector documents. The wire shape
(the NewImage a handler folds) is identical either way, so swapping in a real Lambda-hosted handler
later is a hosting change, not a rewrite.

Deliberate demo simplifications (not production streaming code): shard iterators live only in memory
(no checkpoint store, so a restart re-reads from ``TRIM_HORIZON`` — safe because
:meth:`BookPositionsStore.apply` is idempotent per (book, version)); shard splits/merges are handled
by simply re-describing the stream each loop rather than walking shard lineage (fine for a single,
low-volume demo table). boto3 is synchronous, so every SDK call is dispatched through
``asyncio.to_thread`` to keep the poller cooperative with the HTTP server on the same event loop.
"""

from __future__ import annotations

import asyncio
import json
import logging
from typing import Any

from benzene.core.mapping import to_request

from contracts import TRADE_BOOKED_EVENT_TYPE, TradeBooked

from .store import BookPositionsStore

_POLL_INTERVAL_SECONDS = 1.0
_TABLE_WAIT_INTERVAL_SECONDS = 2.0

logger = logging.getLogger(__name__)


class TradeStreamProjector:
    """A background poller that projects the ledger's DynamoDB Stream into a :class:`BookPositionsStore`."""

    def __init__(
        self,
        dynamodb_client: Any,
        streams_client: Any,
        store: BookPositionsStore,
        table_name: str,
    ) -> None:
        self._dynamodb = dynamodb_client
        self._streams = streams_client
        self._store = store
        self._table_name = table_name

    async def run(self, stop: asyncio.Event) -> None:
        """Poll until ``stop`` is set. Never raises out of the loop — logs and keeps polling."""
        stream_arn = await self._wait_for_stream_arn(stop)
        if stream_arn is None:
            return
        logger.info("Projecting from stream %s", stream_arn)

        shard_iterators: dict[str, str | None] = {}
        poll_count = 0
        while not stop.is_set():
            poll_count += 1
            try:
                await self._poll_once(stream_arn, shard_iterators)
            except Exception as ex:  # noqa: BLE001 - a failed poll must not end the projector silently
                logger.error("Poll %d failed - will retry: %s", poll_count, ex)
            await _sleep_or_stop(stop, _POLL_INTERVAL_SECONDS)

    async def _poll_once(
        self, stream_arn: str, shard_iterators: dict[str, str | None]
    ) -> None:
        description = await asyncio.to_thread(
            self._streams.describe_stream, StreamArn=stream_arn
        )
        for shard in description["StreamDescription"].get("Shards", []):
            shard_id = shard["ShardId"]
            if shard_id in shard_iterators:
                continue
            iterator = await asyncio.to_thread(
                self._streams.get_shard_iterator,
                StreamArn=stream_arn,
                ShardId=shard_id,
                ShardIteratorType="TRIM_HORIZON",
            )
            shard_iterators[shard_id] = iterator["ShardIterator"]
            logger.info("Started tracking shard %s", shard_id)

        for shard_id in list(shard_iterators):
            iterator = shard_iterators[shard_id]
            if iterator is None:
                # A null NextShardIterator means the shard is closed and fully drained.
                continue
            records = await asyncio.to_thread(
                self._streams.get_records, ShardIterator=iterator
            )
            for record in records.get("Records", []):
                self._apply(record)
            shard_iterators[shard_id] = records.get("NextShardIterator")

    def _apply(self, record: dict[str, Any]) -> None:
        if record.get("eventName") != "INSERT":
            return
        image = (record.get("dynamodb") or {}).get("NewImage")
        if not image:
            return
        if image.get("eventType", {}).get("S") != TRADE_BOOKED_EVENT_TYPE:
            return
        payload = image.get("payload", {}).get("S")
        version_raw = image.get("version", {}).get("N")
        if not payload or version_raw is None:
            return
        version = int(version_raw)

        try:
            trade = to_request(TradeBooked, json.loads(payload))
        except (ValueError, TypeError):
            logger.warning("Skipping unparseable TradeBooked payload at version %d", version)
            return

        self._store.apply(trade, version)
        logger.info(
            "Projected trade %s into book %s at version %d",
            trade.trade_id,
            trade.book,
            version,
        )

    async def _wait_for_stream_arn(self, stop: asyncio.Event) -> str | None:
        """Wait for the table's LatestStreamArn (the provisioner may still be running)."""
        while not stop.is_set():
            try:
                table = await asyncio.to_thread(
                    self._dynamodb.describe_table, TableName=self._table_name
                )
                stream_arn = table["Table"].get("LatestStreamArn")
                if stream_arn:
                    return stream_arn
            except self._dynamodb.exceptions.ResourceNotFoundException:
                pass  # the table's provisioning step may still be running
            except Exception as ex:  # noqa: BLE001 - warmup window: retry rather than crash
                logger.info("Waiting for the %s table's stream: %s", self._table_name, ex)
            logger.info(
                "Waiting for the %s table (and its stream) to be provisioned...", self._table_name
            )
            await _sleep_or_stop(stop, _TABLE_WAIT_INTERVAL_SECONDS)
        return None


async def _sleep_or_stop(stop: asyncio.Event, seconds: float) -> None:
    """Sleep for ``seconds``, or wake early if ``stop`` is set (so shutdown is prompt)."""
    try:
        await asyncio.wait_for(stop.wait(), timeout=seconds)
    except asyncio.TimeoutError:
        pass
