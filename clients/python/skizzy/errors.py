"""Errors raised by the skizzy client."""
from __future__ import annotations

from typing import Any, Optional

# Codes never change for a kind (03 protocol section 9).
KIND_BY_CODE = {
    -32700: "PARSE_ERROR", -32600: "INVALID_REQUEST", -32601: "METHOD_NOT_FOUND", -32602: "INVALID_PARAMS",
    -32603: "INTERNAL_ERROR", -32001: "UNAUTHENTICATED", -32002: "PERMISSION_DENIED",
    -32003: "CONFIRMATION_REQUIRED", -32004: "UNAVAILABLE_IN_SCENE", -32005: "NOT_FOUND", -32006: "BUSY",
    -32007: "TIMEOUT", -32008: "CONFLICT", -32009: "PRECONDITION_FAILED", -32010: "PROVIDER_ERROR",
    -32011: "CANCELLED", -32012: "RATE_LIMITED", -32013: "VERSION_UNSUPPORTED", -32020: "LEASE_HELD",
    -32021: "ROE_VIOLATION", -32022: "STALE_DECISION", -32023: "BUDGET_EXHAUSTED", -32024: "SLOW_CONSUMER",
    -32025: "NOT_IN_VIEW", -32026: "EXECUTOR_UNAVAILABLE", -32027: "KILL_SWITCH_ACTIVE", -32028: "DUPLICATE_NAME",
}


class SkizzyError(Exception):
    """A protocol error from the host, or a client-side failure (kind CONNECT_FAILED, NOT_CONNECTED,
    DISCONNECTED, NO_ENDPOINT, NO_TOKEN, TIMEOUT).

    Attributes: ``kind`` (e.g. ``NOT_FOUND``), ``reason``, ``hint``, ``code`` (JSON-RPC code or None),
    ``data`` (the raw ``error.data`` dict), ``message``, ``retryable``.
    """

    def __init__(self, kind: str, message: str = "", reason: Optional[str] = None, hint: Optional[str] = None,
                 code: Optional[int] = None, data: Optional[dict] = None):
        super().__init__(message or kind)
        self.kind = kind
        self.message = message or kind
        self.reason = reason
        self.hint = hint
        self.code = code
        self.data = data or {}

    @property
    def retryable(self) -> Optional[str]:
        return self.data.get("retryable")

    @classmethod
    def from_error_object(cls, err: Any) -> "SkizzyError":
        if not isinstance(err, dict):
            return cls("INTERNAL_ERROR", "malformed error from host")
        data = err.get("data") if isinstance(err.get("data"), dict) else {}
        code = err.get("code") if isinstance(err.get("code"), int) else None
        kind = data.get("kind") or KIND_BY_CODE.get(code, "INTERNAL_ERROR")
        return cls(kind, str(err.get("message") or kind), data.get("reason"), data.get("hint"), code, data)

    def __str__(self) -> str:
        head = self.kind
        if self.reason:
            head += ": " + str(self.reason)
        if self.hint:
            head += " - " + str(self.hint)
        if self.message and self.message != self.kind:
            return head + "\n" + self.message
        return head

    def __repr__(self) -> str:
        return "SkizzyError(%r, %r)" % (self.kind, self.message)


class SkizzyConnectionError(SkizzyError):
    """Could not connect, or the connection is gone (kinds CONNECT_FAILED, NOT_CONNECTED, DISCONNECTED,
    NO_ENDPOINT, NO_TOKEN, UNAUTHENTICATED during connect)."""
