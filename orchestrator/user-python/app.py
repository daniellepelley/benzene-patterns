"""User core service of the orchestrator pattern example, in Python.

A core service is "a guarded database" (docs/patterns/core-services.md): it owns one aggregate,
exposes a small set of topics over it, and holds no cross-service process logic — that lives in the
signup orchestrator, which calls this service.

Two topics, paired the same way the Go tenant service's are: ``user:create`` is the forward action of
the saga's second step and ``user:delete`` is its compensation. ``user:delete`` is idempotent —
deleting an unknown user succeeds — because a compensation may run after a partial failure and may be
retried (docs/patterns/orchestrators.md, "Designing compensations").

This service is reachable only over the Cloud Service Profile's wire-envelope endpoint
(``/benzene/invoke``) and its health endpoint. It declares no HTTP route table on purpose: a core
service here is addressed by **topic**, not URL, which is exactly what lets the .NET orchestrator call
this Python service and the Go tenant service through identical client code.
"""

from __future__ import annotations

import itertools
import os
from collections.abc import Mapping
from dataclasses import dataclass

from benzene.core import (
    AppDefinition,
    BenzeneStartUp,
    Container,
    HealthCheckResult,
    HealthChecks,
    Registry,
    Scope,
    build_application,
)
from benzene.http import BenzeneHttpApp, StandardPaths
from benzene.results import Result

CREATE_USER_TOPIC = "user:create"
DELETE_USER_TOPIC = "user:delete"


@dataclass
class CreateUser:
    tenant_id: str = ""
    email: str = ""


@dataclass
class UserCreated:
    user_id: str
    tenant_id: str
    email: str


@dataclass
class DeleteUser:
    user_id: str = ""


@dataclass
class UserDeleted:
    user_id: str
    deleted: bool


class UserStore:
    """In-memory user store — stands in for a database; swapping it would not touch the handlers."""

    def __init__(self) -> None:
        self.users: dict[str, UserCreated] = {}
        self._by_email: dict[str, str] = {}
        self._ids = itertools.count(1)

    def create(self, tenant_id: str, email: str) -> UserCreated | None:
        """Return the created user, or ``None`` when the email is already taken (a conflict)."""
        if email in self._by_email:
            return None
        user = UserCreated(user_id=f"user-{next(self._ids)}", tenant_id=tenant_id, email=email)
        self.users[user.user_id] = user
        self._by_email[email] = user.user_id
        return user

    def delete(self, user_id: str) -> None:
        user = self.users.pop(user_id, None)
        if user is not None:
            self._by_email.pop(user.email, None)


def make_create_user(store: UserStore):
    async def create_user(request: CreateUser) -> Result:
        if not request.tenant_id:
            return Result.bad_request("tenantId is required")
        if not request.email:
            return Result.bad_request("email is required")

        # The demo's deliberate failure trigger: any address at this domain is rejected, so the
        # orchestrator's saga can be driven down its rollback path on demand without breaking
        # anything else. See the example README's "Watch it roll back".
        if request.email.endswith("@fail.example"):
            return Result.bad_request(f"'{request.email}' is not an acceptable address")

        user = store.create(request.tenant_id, request.email)
        if user is None:
            # A real failure the orchestrator must handle - a Result, not an exception.
            return Result.conflict(f"a user with email '{request.email}' already exists")
        return Result.created(user)

    return create_user


def make_delete_user(store: UserStore):
    async def delete_user(request: DeleteUser) -> Result:
        # Idempotent by design: deleting an unknown id is a success, so a compensation that runs
        # twice (or for a step that never completed) cannot itself fail the rollback and downgrade a
        # clean RolledBack into a PartiallyRolledBack.
        store.delete(request.user_id)
        return Result.ok(UserDeleted(user_id=request.user_id, deleted=True))

    return delete_user


class UserStartUp(BenzeneStartUp):
    """Composition root — the one definition both the host below and any test boots from."""

    def configure_services(self, services: Container, config: Mapping[str, str]) -> None:
        services.try_add_singleton(UserStore)

    def configure(self, services: Scope, config: Mapping[str, str]) -> AppDefinition:
        store = services.get_service(UserStore)

        registry = (
            Registry()
            .register(CREATE_USER_TOPIC, make_create_user(store))
            .register(DELETE_USER_TOPIC, make_delete_user(store))
        )

        health = HealthChecks().add("store", lambda: HealthCheckResult.healthy("in-memory"))

        # No router: this service is addressed by topic over /benzene/invoke only.
        return AppDefinition(
            registry=registry,
            standard_paths=StandardPaths(invoke=True, health=health),
        )


def build_user_app() -> BenzeneHttpApp:
    definition, _ = build_application(UserStartUp)
    return BenzeneHttpApp.from_definition(definition)


app = build_user_app()


if __name__ == "__main__":  # pragma: no cover - container entry point
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=int(os.environ.get("PORT", "8080")))
