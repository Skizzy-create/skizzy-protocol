import json
import os
import threading
from pathlib import Path

import pytest

import skizzy
from conftest import FakeHost, wait_for


# ---------------------------------------------------------------- discovery

def clean_env(monkeypatch):
    for k in ("SKIZZY_ENDPOINT", "KSPDIR"):
        monkeypatch.delenv(k, raising=False)


def test_discovery_via_ksp_folder(host):
    with skizzy.connect(ksp=host.root) as c:
        assert c.session and c.endpoint.url == host.url


def test_discovery_via_endpoint_file(host):
    with skizzy.connect(endpoint_file=host.endpoint_file) as c:
        assert c.state()["scene"] == "FLIGHT"


def test_discovery_via_kspdir_env(host, monkeypatch):
    clean_env(monkeypatch)
    monkeypatch.setenv("KSPDIR", host.root)
    with skizzy.connect() as c:
        assert c.session


def test_discovery_via_skizzy_endpoint_env(host, monkeypatch):
    clean_env(monkeypatch)
    monkeypatch.setenv("SKIZZY_ENDPOINT", host.endpoint_file)
    with skizzy.connect() as c:
        assert c.session


def test_discovery_via_user_pointer(host, monkeypatch, tmp_path):
    clean_env(monkeypatch)
    ptr = tmp_path / "skizzy-protocol"
    ptr.mkdir()
    (ptr / "endpoint.json").write_text(json.dumps({"pluginData": host.info["pluginData"]}))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    with skizzy.connect() as c:
        assert c.session


def test_discovery_fails_clearly(monkeypatch, tmp_path):
    clean_env(monkeypatch)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    with pytest.raises(skizzy.SkizzyError) as e:
        skizzy.connect()
    assert e.value.kind == "NO_ENDPOINT"
    with pytest.raises(skizzy.SkizzyError) as e:
        skizzy.connect(ksp=str(tmp_path))
    assert e.value.kind == "NO_TOKEN"


def test_connect_with_url_and_token(host):
    with skizzy.connect(url=host.url, token=host.token) as c:
        assert c.hello["protocol"] == "skizzy-protocol"


def test_bad_token_is_a_clear_error(host):
    with pytest.raises(skizzy.SkizzyError) as e:
        skizzy.connect(url=host.url, token="not-the-token")
    assert e.value.kind == "UNAUTHENTICATED"
    assert "token" in str(e.value).lower()
    assert "not-the-token" not in str(e.value)


def test_nothing_listening_is_connect_failed():
    with pytest.raises(skizzy.SkizzyError) as e:
        skizzy.connect(url="ws://127.0.0.1:9/skizzy", token="x", timeout=3)
    assert e.value.kind == "CONNECT_FAILED"


# ---------------------------------------------------------------- hello and calls

def test_hello_result(sk):
    h = sk.hello
    assert h["protocol"] == "skizzy-protocol" and h["version"] == "1.0"
    assert h["session"] == sk.session and sk.session
    assert h["role"] == "script"
    assert "eventCursor" in h and h["hostVersion"]
    assert sk.last_meta["scene"] == "FLIGHT"


def test_state_and_meta(sk):
    st = sk.state()
    assert st["scene"] == "FLIGHT" and st["paused"] is False
    assert {"ut", "frame", "scene", "paused", "timeScale", "warp"} <= set(sk.last_meta)


def test_vessels_json_default(sk):
    r = sk.vessels()
    assert isinstance(r["vessels"], list) and len(r["vessels"]) >= 14
    v = r["vessels"][0]
    assert v["handle"].startswith("v:") and v["name"]


def test_vessels_compact(sk):
    r = sk.vessels(format="compact")
    assert "vessels" not in r
    assert r["text"].startswith("#vessels")


def test_vessels_name_like_snake_case_kwargs(sk):
    r = sk.vessels(name_like="DDG*")
    names = [v["name"] for v in r["vessels"]]
    assert names and all(n.startswith("DDG") for n in names)


def test_read_by_name_case_insensitive(sk):
    r = sk.read("ddg-1 kirov")
    assert r["name"] == "DDG-1 Kirov"
    assert r["fields"]["altitude"] is not None
    assert sk.read("DDG-1 KIROV")["handle"] == r["handle"]
    assert sk.read()["handle"] == r["handle"]  # "active"
    assert sk.read(r["handle"], depth=1)["handle"] == r["handle"]


def test_read_not_found_maps_to_error(sk):
    with pytest.raises(skizzy.SkizzyError) as e:
        sk.read("no-such-vessel")
    err = e.value
    assert err.kind == "NOT_FOUND" and err.code == -32005
    assert err.hint and err.reason
    assert err.data["kind"] == "NOT_FOUND"
    assert str(err).startswith("NOT_FOUND: %s - %s" % (err.reason, err.hint))
    assert "no-such-vessel" in err.message


def test_unknown_param_is_invalid_params(sk):
    with pytest.raises(skizzy.SkizzyError) as e:
        sk.vessels(bogus=1)
    assert e.value.kind == "INVALID_PARAMS"
    assert e.value.data["errors"][0]["pointer"] == "/bogus"


def test_unknown_method(sk):
    with pytest.raises(skizzy.SkizzyError) as e:
        sk.call("ksp.vessels.lst")
    assert e.value.kind == "METHOD_NOT_FOUND"
    assert e.value.data.get("didYouMean") == "ksp.vessels.list"


def test_call_params_and_kwargs_merge(sk):
    r = sk.call("ksp.vessels.list", {"limit": 2}, include_debris=True)
    assert len(r["vessels"]) == 2


def test_query(sk):
    r = sk.query(["altitude", "srfSpeed"], {"vessels": {"nameLike": "DDG*"}})
    assert r["columns"][-2:] == ["altitude", "srfSpeed"]
    assert r["rows"] and all(row[1].startswith("DDG") for row in r["rows"])


def test_screenshot_png_bytes(sk, tmp_path):
    out = tmp_path / "shot.png"
    data = sk.screenshot(str(out), format="png", width=512)
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    assert out.read_bytes() == data


def test_screenshot_rate_limit_retry(sk):
    # The simulator renders PNG whatever `format` says (SimRuntime), so only check that it is an image.
    magics = (b"\x89PNG", b"\xff\xd8")
    assert sk.screenshot()[:4].startswith(magics)
    assert sk.screenshot()[:4].startswith(magics)  # 1 capture per 2 s: the helper waits and retries


def test_timeout_is_client_side_error(sk):
    with pytest.raises(skizzy.SkizzyError) as e:
        sk.call("events.wait", timeout_s=3, timeout=0.2)
    assert e.value.kind == "TIMEOUT"
    assert sk.state()["scene"] == "FLIGHT"  # connection still usable


def test_concurrent_calls_are_thread_safe(sk):
    results, errors = [], []

    def work():
        try:
            for _ in range(10):
                results.append(sk.state()["scene"])
        except Exception as e:  # noqa: BLE001
            errors.append(e)

    ts = [threading.Thread(target=work) for _ in range(8)]
    [t.start() for t in ts]
    [t.join(30) for t in ts]
    assert not errors and results == ["FLIGHT"] * 80


# ---------------------------------------------------------------- logs

def test_log_start_stop_writes_csv(sk, host):
    name = "pytest-%d" % os.getpid()
    started = sk.log_start(name, ["altitude", "srfSpeed"], rate_hz=20, selector={"vessels": {"nameLike": "DDG*"}})
    assert started["path"].endswith("%s.csv" % name)
    assert "altitude" in started["columns"]
    f = Path(host.root) / started["path"]
    assert Path(host.root, "Skizzy", "logs") in f.parents
    assert wait_for(lambda: any(l["name"] == name and l["rows"] > 0 for l in sk.logs()["logs"]), 10)
    tail = sk.log_tail(name, n=2)
    assert tail["rows"] >= 1
    stopped = sk.log_stop(name)
    assert stopped["state"] == "stopped" and stopped["rows"] > 0
    assert f.exists()
    lines = f.read_text(encoding="utf-8-sig").splitlines()  # the host writes a UTF-8 BOM
    assert lines[0].split(",")[:2] == ["ut", "frame"] and "altitude" in lines[0]
    assert len(lines) > 1
    assert any(l["name"] == name and l["state"] == "stopped" for l in sk.logs()["logs"])


# ---------------------------------------------------------------- events

def test_events_poll_and_wait(sk):
    r = sk.events_poll(limit=5)
    assert isinstance(r["events"], list) and "nextCursor" in r
    assert sk.event_cursor >= r["nextCursor"]
    w = sk.events_wait(["host.*", "rec.*"], timeout_s=0.5, cursor=r["nextCursor"])
    assert w["events"] == [] and w["timedOut"] is True


def test_events_wait_wakes_on_event(sk):
    name = "evt-%d" % os.getpid()
    cur = sk.events_poll(limit=1)["nextCursor"]

    def later():
        sk.log_start(name, ["altitude"])
        sk.log_stop(name)

    t = threading.Timer(0.3, later)
    t.start()
    w = sk.events_wait(["rec.*"], timeout_s=10, cursor=cur)
    t.join()
    assert w["events"] and w["events"][0]["name"].startswith("rec.")


def test_events_subscribe_push(sk):
    got = []
    sk.events_subscribe(got.append, ["rec.*"])
    name = "push-%d" % os.getpid()
    sk.log_start(name, ["altitude"])
    sk.log_stop(name)
    assert wait_for(lambda: len(got) >= 2, 10)
    assert got[0]["events"][0]["name"] == "rec.log_started"


# ---------------------------------------------------------------- streams

def test_subscribe_iterates_then_closes(sk):
    sub = sk.subscribe(["altitude"], {"vessels": {"nameLike": "DDG*"}}, rate_hz=20)
    assert sub.id.startswith("sub:") and sub.initial["rows"]
    msgs = [sub.get(5) for _ in range(3)]
    assert [m["seq"] for m in msgs] == sorted(m["seq"] for m in msgs)
    assert all(m["sub"] == sub.id and m["rows"] for m in msgs)
    sub.close()
    assert sub.closed
    assert sk.call("stream.list")["total"] == 0
    sub.close()  # idempotent


def test_subscribe_for_loop_ends_on_close(sk):
    sub = sk.subscribe(["altitude"], rate_hz=20)
    seen = []
    for msg in sub:
        seen.append(msg)
        if len(seen) == 3:
            threading.Thread(target=sub.close).start()
    assert len(seen) >= 3


def test_subscribe_callback(sk):
    got = []
    sub = sk.subscribe(["altitude"], rate_hz=20, callback=got.append)
    assert wait_for(lambda: len(got) >= 3, 10)
    sub.close()
    assert all(m["sub"] == sub.id for m in got)


def test_subscription_get_timeout(sk):
    sub = sk.subscribe(["altitude"], rate_hz=0.1)
    with pytest.raises(TimeoutError):
        sub.get(0.05)
    sub.close()


def test_close_is_idempotent_and_blocks_calls(host):
    c = skizzy.connect(ksp=host.root)
    c.close()
    c.close()
    with pytest.raises(skizzy.SkizzyError) as e:
        c.state()
    assert e.value.kind == "NOT_CONNECTED"


# ---------------------------------------------------------------- reconnect

def test_auto_reconnect_after_host_restart(tmp_path):
    h = FakeHost(tmp_path).start()
    c = None
    try:
        c = skizzy.connect(ksp=h.root, auto_reconnect=True)
        first_session, first_url = c.session, c.endpoint.url
        sub = c.subscribe(["altitude"], rate_hz=20)
        sub.get(5)
        dropped = []
        c.on_disconnect = dropped.append
        h.stop()
        assert wait_for(lambda: not c.connected, 10)
        h = FakeHost(tmp_path).start()  # same root dir, new port
        assert h.url != first_url
        assert wait_for(lambda: c.connected and c.reconnects == 1, 30)
        assert c.endpoint.url == h.url  # endpoint re-discovered
        assert c.session != first_session or c.hello["resumed"]
        assert c.state()["scene"] == "FLIGHT"
        assert dropped
        # the subscription was re-created on the new connection
        msg = sub.get(10)
        assert msg["rows"]
        sub.close()
    finally:
        if c:
            c.close()
        h.stop()


def test_no_reconnect_by_default_fails_calls(tmp_path):
    h = FakeHost(tmp_path).start()
    try:
        c = skizzy.connect(ksp=h.root)
        h.stop()
        assert wait_for(lambda: not c.connected, 10)
        with pytest.raises(skizzy.SkizzyError) as e:
            c.state()
        assert e.value.kind == "NOT_CONNECTED"
        c.close()
    finally:
        h.stop()


def test_error_string_format():
    e = skizzy.SkizzyError("NOT_FOUND", "Nothing matches 'x'", "unknown", "list again", -32005)
    assert str(e) == "NOT_FOUND: unknown - list again\nNothing matches 'x'"
    assert str(skizzy.SkizzyError("BUSY")) == "BUSY"
