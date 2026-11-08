"""Synchronous, thread-safe client for the skizzy host (JSON-RPC 2.0 over WebSocket)."""
from __future__ import annotations

import base64
import collections
import inspect
import json
import queue
import threading
import time
from typing import Any, Callable, Dict, Iterator, List, Optional

from websockets.exceptions import ConnectionClosed, InvalidHandshake, InvalidStatus
from websockets.sync.client import connect as _ws_connect

from . import __version__
from .discovery import Endpoint, discover
from .errors import SkizzyConnectionError, SkizzyError

SUBPROTOCOL = "skizzy.v1"
PROTOCOL = "skizzy-protocol"
BACKOFF = (0.5, 1.0, 2.0, 4.0, 10.0)
HELLO_TIMEOUT = 10.0

_WS_PARAMS = set(inspect.signature(_ws_connect).parameters)


def _camel(name: str) -> str:
    if name.startswith("_") or "_" not in name:
        return name
    head, *rest = name.split("_")
    return head + "".join(p[:1].upper() + p[1:] for p in rest)


def _merge(params: Optional[dict], kw: Dict[str, Any]) -> dict:
    if params is None:
        p: dict = {}
    elif isinstance(params, dict):
        p = dict(params)
    else:
        raise TypeError("params must be a dict (the protocol uses named parameters)")
    for k, v in kw.items():
        if v is not None:
            p[_camel(k)] = v
    return p


class _Pending:
    __slots__ = ("event", "result", "error")

    def __init__(self) -> None:
        self.event = threading.Event()
        self.result: Any = None
        self.error: Optional[BaseException] = None


class Subscription:
    """A ``stream.subscribe`` subscription. Iterate it, call ``get(timeout)``, or pass ``callback=`` to
    ``Client.subscribe``. Each message is the ``stream.data`` params dict: ``{sub, seq, ut, kind, rows, meta}``.
    ``initial`` is the full snapshot returned by the subscribe call itself."""

    def __init__(self, client: "Client", spec: dict, result: dict, callback: Optional[Callable[[dict], None]],
                 queue_size: int = 1000):
        self._client = client
        self._spec = spec
        self._callback = callback
        self._q: "queue.Queue[Any]" = queue.Queue(maxsize=queue_size)
        self._closed = False
        self._set_result(result)

    _END = object()

    def _set_result(self, result: dict) -> None:
        self.id: str = result.get("sub")
        self.initial: dict = result
        self.columns: List[str] = result.get("columns") or []
        self.effective_rate_hz = result.get("effectiveRateHz")

    @property
    def closed(self) -> bool:
        return self._closed

    def _deliver(self, params: dict) -> None:
        if self._closed:
            return
        if self._callback is not None:
            self._client._dispatch_callback(self._callback, params)
            return
        while True:
            try:
                self._q.put_nowait(params)
                return
            except queue.Full:  # slow consumer: drop the oldest, keep the latest state
                try:
                    self._q.get_nowait()
                except queue.Empty:
                    pass

    def _finish(self) -> None:
        self._closed = True
        try:
            self._q.put_nowait(self._END)
        except queue.Full:
            try:
                self._q.get_nowait()
                self._q.put_nowait(self._END)
            except (queue.Empty, queue.Full):
                pass

    def get(self, timeout: Optional[float] = None) -> dict:
        """Next ``stream.data`` message. Raises TimeoutError on timeout, StopIteration-like
        ``SkizzyError(kind='CLOSED')`` if the subscription was closed."""
        try:
            item = self._q.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError("no stream.data within %s s" % timeout) from None
        if item is self._END:
            try:
                self._q.put_nowait(self._END)  # keep ending further get() calls
            except queue.Full:
                pass
            raise SkizzyError("CLOSED", "subscription %s is closed" % self.id)
        return item

    def __iter__(self) -> Iterator[dict]:
        while True:
            try:
                yield self.get()
            except SkizzyError as e:
                if e.kind == "CLOSED":
                    return
                raise

    def close(self) -> None:
        """Unsubscribe (stream.unsubscribe) and end any iterator. Safe to call twice."""
        if self._closed:
            return
        self._client._forget_sub(self)
        self._finish()
        try:
            self._client.call("stream.unsubscribe", {"sub": self.id}, timeout=5)
        except SkizzyError:
            pass

    def __enter__(self) -> "Subscription":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()


class Client:
    """Connect with :func:`skizzy.connect`. All methods are thread-safe."""

    def __init__(self, endpoint: Callable[[], Endpoint], *, name: str = "skizzy-py", actor_id: Optional[str] = None,
                 actor_kind: str = "script", role: Optional[str] = None, side: Optional[str] = None,
                 features: Optional[List[str]] = None, auto_reconnect: bool = False,
                 connect_timeout: float = 10.0, max_message_bytes: int = 64 * 1024 * 1024):
        self._endpoint_source = endpoint
        self._hello_opts = dict(name=name, actor_id=actor_id, actor_kind=actor_kind, role=role, side=side,
                                features=list(features or ["ops", "streams", "events"]))
        self.auto_reconnect = auto_reconnect
        self._connect_timeout = connect_timeout
        self._max_size = max_message_bytes
        self._lock = threading.RLock()
        self._send_lock = threading.Lock()
        self._pending: Dict[int, _Pending] = {}
        self._next_id = 0
        self._ws = None
        self._connected = threading.Event()
        self._closed = False
        self._stop = threading.Event()
        self._reconnecting = False
        self._subs: Dict[str, Subscription] = {}
        self._orphans: "collections.OrderedDict[str, List[dict]]" = collections.OrderedDict()
        self._recent_closed: "collections.deque[str]" = collections.deque(maxlen=64)
        self._event_cbs: List[Callable[[dict], None]] = []
        self._notif_cbs: List[Callable[[str, dict], None]] = []
        self._work: "queue.Queue[Any]" = queue.Queue()
        self._worker: Optional[threading.Thread] = None

        self.hello: dict = {}
        self.session: Optional[str] = None
        self.last_meta: dict = {}
        self.event_cursor: int = 0
        self.last_error: Optional[str] = None
        self.reconnects = 0
        self.endpoint: Optional[Endpoint] = None
        #: optional hooks, called on the callback thread
        self.on_disconnect: Optional[Callable[[str], None]] = None
        self.on_reconnect: Optional[Callable[["Client"], None]] = None

    # ------------------------------------------------------------------ connecting

    @property
    def connected(self) -> bool:
        return self._connected.is_set()

    def _connect(self) -> None:
        self._open(None)

    def _hello_params(self, resume: bool) -> dict:
        o = self._hello_opts
        actor: Dict[str, Any] = {"kind": o["actor_kind"]}
        for k_out, k_in in (("id", "actor_id"), ("role", "role"), ("side", "side")):
            if o[k_in] is not None:
                actor[k_out] = o[k_in]
        p: Dict[str, Any] = {
            "protocol": PROTOCOL,
            "versions": ["1.0"],
            "client": {"name": o["name"], "version": __version__, "kind": "sdk"},
            "actor": actor,
            "features": o["features"],
        }
        if resume and self.session:
            p["resume"] = {"session": self.session, "fromEventCursor": self.event_cursor}
        return p

    def _open(self, resume: bool) -> None:
        ep = self._endpoint_source()
        self.endpoint = ep
        kwargs: Dict[str, Any] = dict(
            subprotocols=[SUBPROTOCOL],  # type: ignore[list-item]
            additional_headers={"Authorization": "Bearer " + ep.token},
            open_timeout=self._connect_timeout,
            max_size=self._max_size,
            compression=None,
            proxy=None,        # websockets >= 15: never route localhost through a system proxy
            ping_interval=20,  # websockets >= 15
            ping_timeout=60,
            legacy=True,       # websockets >= 17: return the connection itself, not a context manager
        )
        kwargs = {k: v for k, v in kwargs.items() if k in _WS_PARAMS}
        try:
            ws = _ws_connect(ep.url, **kwargs)  # no Origin header: the host refuses Origin-bearing connections
            if not hasattr(ws, "recv") and hasattr(ws, "__enter__"):
                ws = ws.__enter__()
        except InvalidStatus as e:
            code = getattr(getattr(e, "response", None), "status_code", None)
            if code in (401, 403):
                raise SkizzyConnectionError(
                    "UNAUTHENTICATED", "host at %s refused the connection (HTTP %s)" % (ep.url, code),
                    hint="The token is wrong or stale; re-read auth.token (pass ksp= / endpoint_file=).") from None
            raise SkizzyConnectionError("CONNECT_FAILED", "cannot connect to %s: HTTP %s" % (ep.url, code),
                                        hint="Is this a skizzy host? Check the url.") from None
        except (OSError, TimeoutError, InvalidHandshake, ValueError) as e:
            raise SkizzyConnectionError(
                "CONNECT_FAILED", "cannot connect to %s: %s" % (ep.url, e),
                hint="Is KSP running with Skizzy loaded? Check the in-game Skizzy window for the port.") from None
        with self._lock:
            self._ws = ws
        threading.Thread(target=self._read_loop, args=(ws,), name="skizzy-reader", daemon=True).start()
        try:
            res = self._request(ws, "session.hello", self._hello_params(resume), HELLO_TIMEOUT)
        except BaseException as e:
            with self._lock:
                if self._ws is ws:
                    self._ws = None
            try:
                ws.close()
            except Exception:
                pass
            if isinstance(e, SkizzyError) and not isinstance(e, SkizzyConnectionError):
                if e.kind == "UNAUTHENTICATED":
                    e.hint = e.hint or "The token is wrong or stale; re-read auth.token."
                raise SkizzyConnectionError(e.kind, e.message, e.reason, e.hint, e.code, e.data) from None
            raise
        self._after_hello(res)

    def _after_hello(self, res: dict) -> None:
        self.hello = res
        self.session = res.get("session")
        if not res.get("resumed"):
            self.event_cursor = int(res.get("eventCursor") or 0)
        self._note_meta(res)
        self.last_error = None
        self._connected.set()

    # ------------------------------------------------------------------ transport

    def _note_meta(self, res: Any) -> None:
        if isinstance(res, dict) and isinstance(res.get("meta"), dict):
            self.last_meta = res["meta"]

    def _request(self, ws: Any, method: str, params: dict, timeout: Optional[float]) -> Any:
        pend = _Pending()
        with self._lock:
            self._next_id += 1
            rid = self._next_id
            self._pending[rid] = pend
        text = json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}, separators=(",", ":"))
        try:
            with self._send_lock:
                ws.send(text)
        except ConnectionClosed as e:
            with self._lock:
                self._pending.pop(rid, None)
            raise SkizzyConnectionError("DISCONNECTED", "connection lost: %s" % e,
                                        hint="Retry; use auto_reconnect=True to survive KSP restarts.") from None
        except Exception as e:
            with self._lock:
                self._pending.pop(rid, None)
            raise SkizzyConnectionError("DISCONNECTED", "send failed: %s" % e, hint="The connection dropped; retry.") from None
        if not pend.event.wait(timeout):
            with self._lock:
                self._pending.pop(rid, None)
            raise SkizzyError("TIMEOUT", "%s timed out after %s s (client side)" % (method, timeout),
                              hint="The host may be busy or paused; retry.", data={"timeoutMs": int((timeout or 0) * 1000)})
        if pend.error is not None:
            raise pend.error
        return pend.result

    def _read_loop(self, ws: Any) -> None:
        reason = "closed"
        try:
            while True:
                msg = ws.recv()
                self._dispatch(msg)
        except ConnectionClosed as e:
            rcvd = getattr(e, "rcvd", None)
            reason = "host closed: %s %s" % (getattr(rcvd, "code", "?"), getattr(rcvd, "reason", "")) if rcvd else "connection closed"
        except Exception as e:  # noqa: BLE001
            reason = "%s: %s" % (type(e).__name__, e)
        self._on_dropped(ws, reason)

    def _dispatch(self, raw: Any) -> None:
        if isinstance(raw, (bytes, bytearray)):
            raw = bytes(raw).decode("utf-8", "replace")
        try:
            m = json.loads(raw)
        except ValueError:
            return
        if not isinstance(m, dict):
            return
        if "method" in m and "id" not in m:
            self._notification(str(m["method"]), m.get("params") if isinstance(m.get("params"), dict) else {})
            return
        rid = m.get("id")
        if not isinstance(rid, int):
            return
        with self._lock:
            pend = self._pending.pop(rid, None)
        if pend is None:
            return
        if isinstance(m.get("error"), dict):
            pend.error = SkizzyError.from_error_object(m["error"])
        else:
            res = m.get("result")
            pend.result = res if res is not None else {}
            self._note_meta(res)
        pend.event.set()

    def _notification(self, method: str, params: dict) -> None:
        if method == "stream.data":
            sid = params.get("sub")
            with self._lock:
                sub = self._subs.get(sid)
                if sub is None:
                    if sid in self._recent_closed:
                        return
                    # data can overtake the subscribe result (host sends the result from a pool thread)
                    self._orphans.setdefault(sid, []).append(params)
                    if len(self._orphans[sid]) > 64:
                        del self._orphans[sid][0]
                    while len(self._orphans) > 16:
                        self._orphans.popitem(last=False)
                    return
            sub._deliver(params)
        elif method == "events.data":
            nc = params.get("nextCursor")
            if isinstance(nc, int) and nc > self.event_cursor:
                self.event_cursor = nc
            for cb in list(self._event_cbs):
                self._dispatch_callback(cb, params)
        for cb in list(self._notif_cbs):
            self._dispatch_callback(cb, method, params)

    def _dispatch_callback(self, fn: Callable[..., Any], *args: Any) -> None:
        """Callbacks run on one worker thread so they may call back into the client."""
        with self._lock:
            if self._worker is None:
                self._worker = threading.Thread(target=self._work_loop, name="skizzy-callbacks", daemon=True)
                self._worker.start()
        self._work.put((fn, args))

    def _work_loop(self) -> None:
        while True:
            item = self._work.get()
            if item is None:
                return
            fn, args = item
            try:
                fn(*args)
            except Exception:  # noqa: BLE001 - a bad callback must not kill the dispatcher
                import traceback
                traceback.print_exc()

    def _on_dropped(self, ws: Any, reason: str) -> None:
        with self._lock:
            if ws is not self._ws:
                return
            self._ws = None
            self._connected.clear()
            self.last_error = reason
            pend = list(self._pending.values())
            self._pending.clear()
            closed = self._closed
        err = SkizzyConnectionError("DISCONNECTED", "connection lost: %s" % reason,
                                    hint="Retry; the client reconnects automatically if auto_reconnect=True.")
        for p in pend:
            p.error = err
            p.event.set()
        if closed:
            return
        if self.on_disconnect:
            self._dispatch_callback(self.on_disconnect, reason)
        if self.auto_reconnect:
            self._start_reconnect()
        else:
            self._finish_subs()

    def _finish_subs(self) -> None:
        with self._lock:
            subs = list(self._subs.values())
            self._subs.clear()
        for s in subs:
            s._finish()

    def _start_reconnect(self) -> None:
        with self._lock:
            if self._reconnecting or self._closed:
                return
            self._reconnecting = True
        threading.Thread(target=self._reconnect_loop, name="skizzy-reconnect", daemon=True).start()

    def _reconnect_loop(self) -> None:
        try:
            attempt = 0
            while not self._closed:
                if self._stop.wait(BACKOFF[min(attempt, len(BACKOFF) - 1)]):
                    return
                attempt += 1
                try:
                    self._open(True)  # re-discovers the endpoint: the port can change after a KSP restart
                except SkizzyError as e:
                    self.last_error = str(e)
                    continue
                except Exception as e:  # noqa: BLE001
                    self.last_error = "%s: %s" % (type(e).__name__, e)
                    continue
                self.reconnects += 1
                self._resubscribe()
                if self.on_reconnect:
                    self._dispatch_callback(self.on_reconnect, self)
                return
        finally:
            with self._lock:
                self._reconnecting = False
            # the connection may have dropped again between _open and here
            if not self._closed and self.auto_reconnect and not self._connected.is_set():
                self._start_reconnect()

    def _resubscribe(self) -> None:
        """Streams die with the old connection; re-create the ones the host no longer lists."""
        with self._lock:
            subs = list(self._subs.values())
        if not subs:
            return
        try:
            alive = {s.get("sub") for s in (self.call("stream.list", timeout=10).get("streams") or [])}
        except SkizzyError:
            alive = set()
        for sub in subs:
            if sub.id in alive:
                continue
            try:
                res = self.call("stream.subscribe", sub._spec, timeout=10)
            except SkizzyError:
                continue
            with self._lock:
                self._subs.pop(sub.id, None)
                sub._set_result(res)
                self._subs[sub.id] = sub

    # ------------------------------------------------------------------ public API

    def call(self, method: str, params: Optional[dict] = None, timeout: Optional[float] = 60, **kw: Any) -> dict:
        """Call any capability or protocol method. ``kw`` merge into params (snake_case becomes camelCase,
        None values are dropped). Returns the result dict (which includes ``meta``); raises SkizzyError."""
        p = _merge(params, kw)
        if not self._connected.is_set():
            if self.auto_reconnect and not self._closed:
                self._connected.wait(timeout)
            if not self._connected.is_set():
                raise SkizzyConnectionError("NOT_CONNECTED", "not connected to the host",
                                            hint=self.last_error or "Is KSP running? Is the endpoint right?")
        with self._lock:
            ws = self._ws
        if ws is None:
            raise SkizzyConnectionError("NOT_CONNECTED", "not connected to the host", hint=self.last_error)
        return self._request(ws, method, p, timeout)

    def state(self, **kw: Any) -> dict:
        return self.call("host.state", **kw)

    def vessels(self, **kw: Any) -> dict:
        return self.call("ksp.vessels.list", **kw)

    def read(self, vessel: str = "active", **kw: Any) -> dict:
        return self.call("ksp.vessel.get", vessel=vessel, **kw)

    def query(self, fields: List[str], selector: Optional[dict] = None, **kw: Any) -> dict:
        return self.call("ksp.query", fields=list(fields), select=selector, **kw)

    def screenshot(self, path: Optional[str] = None, **kw: Any) -> bytes:
        """Take a screenshot; returns the image bytes (JPEG by default, ``format="png"`` for PNG)."""
        res: Optional[dict] = None
        for attempt in range(4):
            try:
                res = self.call("ksp.camera.screenshot", **kw)
                break
            except SkizzyError as e:
                if e.kind != "RATE_LIMITED" or attempt == 3:  # 1 capture per 2 s per session
                    raise
                time.sleep(min(float(e.data.get("retryAfterMs") or 2000), 5000) / 1000.0 + 0.05)
        img = (res or {}).get("image") or {}
        if not img.get("data"):
            raise SkizzyError("INTERNAL_ERROR", "screenshot result has no image.data")
        data = base64.b64decode(img["data"])
        if path:
            with open(path, "wb") as f:
                f.write(data)
        return data

    def log_start(self, name: str, fields: List[str], rate_hz: float = 5, selector: Optional[dict] = None,
                  **kw: Any) -> dict:
        return self.call("rec.log.start", name=name, fields=list(fields), rate_hz=rate_hz, select=selector, **kw)

    def log_stop(self, name: str) -> dict:
        return self.call("rec.log.stop", log=name)

    def logs(self) -> dict:
        return self.call("rec.log.list")

    def log_tail(self, name: str, n: int = 20, **kw: Any) -> dict:
        return self.call("rec.log.tail", log=name, n=n, **kw)

    def events_poll(self, **kw: Any) -> dict:
        res = self.call("events.poll", **kw)
        self._advance_cursor(res)
        return res

    def events_wait(self, names: Optional[List[str]] = None, timeout_s: float = 10, **kw: Any) -> dict:
        kw.setdefault("format", "json")
        res = self.call("events.wait", names=names, timeout_s=timeout_s, timeout=timeout_s + 15, **kw)
        self._advance_cursor(res)
        return res

    def _advance_cursor(self, res: dict) -> None:
        nc = res.get("nextCursor")
        if isinstance(nc, int) and nc > self.event_cursor:
            self.event_cursor = nc

    def events_subscribe(self, callback: Callable[[dict], None], names: Optional[List[str]] = None) -> dict:
        """Ask the host to push ``events.data``; ``callback(params)`` gets ``{events, nextCursor, meta}``."""
        self._event_cbs.append(callback)
        return self.call("events.subscribe", names=names)

    def on_notification(self, callback: Callable[[str, dict], None]) -> None:
        """Register ``callback(method, params)`` for every notification (stream.data, events.data, ...)."""
        self._notif_cbs.append(callback)

    def subscribe(self, fields: List[str], selector: Optional[dict] = None, rate_hz: float = 5,
                  mode: str = "sampled", callback: Optional[Callable[[dict], None]] = None,
                  aggregate: Optional[str] = None, reduce: Optional[str] = None, **kw: Any) -> Subscription:
        """Subscribe to a sampled (or ``mode="onchange"``) stream over a ksp.query-style selector."""
        q: Dict[str, Any] = {"fields": list(fields)}
        if selector:
            q["select"] = selector
        if aggregate:
            q["aggregate"] = aggregate
        if reduce:
            q["reduce"] = reduce
        spec = _merge({"source": {"query": q}, "mode": mode, "rateHz": rate_hz}, kw)
        res = self.call("stream.subscribe", spec)
        sub = Subscription(self, spec, res, callback)
        with self._lock:
            self._subs[sub.id] = sub
            early = self._orphans.pop(sub.id, [])
        for params in early:
            sub._deliver(params)
        return sub

    def _forget_sub(self, sub: Subscription) -> None:
        with self._lock:
            self._subs.pop(sub.id, None)
            self._recent_closed.append(sub.id)
            self._orphans.pop(sub.id, None)

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            ws = self._ws
            connected = self._connected.is_set()
        self._stop.set()
        if ws is not None and connected:
            try:
                self._request(ws, "session.bye", {}, 1.0)
            except Exception:  # noqa: BLE001
                pass
        if ws is not None:
            try:
                ws.close()
            except Exception:  # noqa: BLE001
                pass
        with self._lock:
            self._ws = None
            self._connected.clear()
            pend = list(self._pending.values())
            self._pending.clear()
        for p in pend:
            p.error = SkizzyConnectionError("DISCONNECTED", "client closed")
            p.event.set()
        self._finish_subs()
        self._work.put(None)

    def __enter__(self) -> "Client":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()

    def __repr__(self) -> str:
        return "<skizzy.Client %s session=%s %s>" % (
            self.endpoint.url if self.endpoint else "-", self.session, "connected" if self.connected else "disconnected")


def connect(url: Optional[str] = None, token: Optional[str] = None, *, endpoint_file: Optional[str] = None,
            ksp: Optional[str] = None, token_file: Optional[str] = None, name: str = "skizzy-py",
            actor_id: Optional[str] = None, actor_kind: str = "script", role: Optional[str] = None,
            side: Optional[str] = None, auto_reconnect: bool = False, timeout: float = 10.0) -> Client:
    """Find the host and say hello. ``connect()`` discovers it (endpoint_file, ksp folder, env SKIZZY_ENDPOINT,
    env KSPDIR, per-user pointer); ``connect(url=..., token=...)`` skips discovery."""
    if url is not None or token is not None:
        if not (url and token):
            raise SkizzyConnectionError("NO_ENDPOINT", "url and token must be given together",
                                        hint="Or omit both to discover the host.")
        fixed = Endpoint(url, token)

        def source() -> Endpoint:
            return fixed
    else:
        def source() -> Endpoint:
            return discover(endpoint_file, ksp, token_file)

    c = Client(source, name=name, actor_id=actor_id, actor_kind=actor_kind, role=role, side=side,
               auto_reconnect=auto_reconnect, connect_timeout=timeout)
    c._connect()
    return c
