"""Test fixtures: spawn the prebuilt FakeKsp host (skizzy-fakehost) and talk to it for real."""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

REPO = Path(__file__).resolve().parents[3]
# Built by: dotnet build tools/Skizzy.FakeHost -c Release   (or point SKIZZY_FAKEHOST at skizzy-fakehost.dll/.exe)
DEFAULT_HOSTS = [
    REPO / "tools" / "Skizzy.FakeHost" / "bin" / "Release" / "net10.0" / "skizzy-fakehost.dll",
    REPO / "tools" / "Skizzy.FakeHost" / "bin" / "Debug" / "net10.0" / "skizzy-fakehost.dll",
]


def host_command():
    path = os.environ.get("SKIZZY_FAKEHOST")
    if not path:
        path = next((str(p) for p in DEFAULT_HOSTS if p.is_file()), str(DEFAULT_HOSTS[0]))
    if not os.path.isfile(path):
        pytest.skip("fake host not found (build tools/Skizzy.FakeHost or set SKIZZY_FAKEHOST): %s" % path)
    return ["dotnet", path] if path.lower().endswith(".dll") else [path]


class FakeHost:
    """One skizzy-fakehost process. Closing its stdin stops it."""

    def __init__(self, directory, vessels=14):
        self.dir = str(directory)
        self.vessels = vessels
        self.proc = None
        self.info = {}

    def start(self):
        cmd = host_command() + ["--dir", self.dir, "--port", "0", "--vessels", str(self.vessels), "--stdin-exit"]
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("fake host exited without printing its ready line")
        self.info = json.loads(line)
        return self

    def stop(self):
        if self.proc is None:
            return
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(15)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(5)
        self.proc = None

    @property
    def url(self):
        return self.info["url"]

    @property
    def root(self):
        return self.info["root"]

    @property
    def endpoint_file(self):
        return self.info["endpointFile"]

    @property
    def token(self):
        return Path(self.info["tokenFile"]).read_text(encoding="utf-8").strip()


@pytest.fixture(scope="session")
def host(tmp_path_factory):
    h = FakeHost(tmp_path_factory.mktemp("fakehost")).start()
    yield h
    h.stop()


@pytest.fixture()
def sk(host):
    import skizzy
    c = skizzy.connect(ksp=host.root, name="skizzy-py-tests")
    yield c
    c.close()


def wait_for(pred, timeout=20.0, step=0.1):
    end = time.time() + timeout
    while time.time() < end:
        if pred():
            return True
        time.sleep(step)
    return pred()
