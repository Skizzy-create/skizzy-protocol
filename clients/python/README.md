# skizzy (Python)

Small synchronous client for skizzy-protocol, the control protocol for Kerbal Space Program.

```
pip install -e .          # from clients/python; needs Python 3.9+ and websockets>=12
```

The host is found via `endpoint.json` next to `auth.token` (arg `ksp=` / `endpoint_file=`, env `SKIZZY_ENDPOINT`,
env `KSPDIR`, or the per-user pointer in `%LOCALAPPDATA%/skizzy-protocol`). The token is never printed.

```python
import skizzy

with skizzy.connect(auto_reconnect=True) as sk:          # or connect(ksp=r"D:\KSP") / connect(url=..., token=...)
    print(sk.state()["scene"], sk.session)
    for v in sk.vessels(name_like="DDG*")["vessels"]:    # snake_case kwargs become camelCase params
        print(v["handle"], v["name"], v["altitude"])
    print(sk.read("ddg-1 kirov")["fields"]["srfSpeed"])  # names are case-insensitive
    sk.screenshot("shot.png", format="png")
    sk.log_start("climb", ["altitude", "srfSpeed"], rate_hz=5)
    with sk.subscribe(["altitude"], rate_hz=10) as sub:
        for msg in sub:                                   # stream.data: {sub, seq, ut, kind, rows}
            print(msg["seq"], msg["rows"][0])
            if msg["seq"] >= 5: break
    sk.log_stop("climb")
    try:
        sk.read("nope")
    except skizzy.SkizzyError as e:                       # e.kind, e.reason, e.hint, e.code, e.data
        print(e)
```

`sk.call("any.capability", **params)` reaches everything else. Run the tests with
`.venv/Scripts/python -m pytest` (they spawn the prebuilt fake host; set `SKIZZY_FAKEHOST` to its path).
