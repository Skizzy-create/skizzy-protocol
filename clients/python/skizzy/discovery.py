"""Finds the running host: endpoint.json (url, port, pid) plus auth.token next to it.

Order (same as ``Endpoint.Discover`` in the C# client): explicit endpoint file, explicit KSP folder,
env ``SKIZZY_ENDPOINT`` (path of endpoint.json), env ``KSPDIR``, the per-user pointer
``%LOCALAPPDATA%/skizzy-protocol/endpoint.json`` whose ``pluginData`` field names the PluginData folder.
The token is never printed or logged.
"""
from __future__ import annotations

import json
import os
from typing import List, Optional

from .errors import SkizzyConnectionError

DEFAULT_PORT = 47549
PATH = "/skizzy"


class Endpoint:
    __slots__ = ("url", "token", "plugin_data")

    def __init__(self, url: str, token: str, plugin_data: Optional[str] = None):
        self.url = url
        self.token = token
        self.plugin_data = plugin_data

    def __repr__(self) -> str:  # never show the token
        return "Endpoint(url=%r, plugin_data=%r)" % (self.url, self.plugin_data)


def plugin_data_for(ksp_root: str) -> str:
    return os.path.join(ksp_root, "GameData", "SkizzyProtocol", "PluginData")


def user_pointer_path() -> Optional[str]:
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        home = os.path.expanduser("~")
        base = os.path.join(home, ".local", "share") if home and home != "~" else None
    return os.path.join(base, "skizzy-protocol", "endpoint.json") if base else None


def _read_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as f:
        v = json.load(f)
    return v if isinstance(v, dict) else {}


def _read_token(path: str) -> str:
    with open(path, "r", encoding="utf-8-sig") as f:
        return f.read().strip()


def from_plugin_data(plugin_data: str, token_file: Optional[str] = None) -> Endpoint:
    tok = token_file or os.path.join(plugin_data, "auth.token")
    if not os.path.isfile(tok):
        msg = "token file not found: %s" % token_file if token_file else "no auth.token in %s" % plugin_data
        raise SkizzyConnectionError(
            "NO_TOKEN", msg,
            hint="Start KSP once with Skizzy installed (it creates the token), or pass token_file=.")
    token = _read_token(tok)
    url = None
    ep = os.path.join(plugin_data, "endpoint.json")
    if os.path.isfile(ep):
        try:
            j = _read_json(ep)
        except (OSError, ValueError) as e:
            raise SkizzyConnectionError("NO_ENDPOINT", "cannot read %s: %s" % (ep, e),
                                        hint="The host may be rewriting it; retry, or pass url=.")
        url = j.get("url") or "ws://127.0.0.1:%d%s" % (int(j.get("port") or DEFAULT_PORT), PATH)
    if not url:
        url = "ws://127.0.0.1:%d%s" % (DEFAULT_PORT, PATH)
    return Endpoint(url, token, plugin_data)


def discover(endpoint_file: Optional[str] = None, ksp: Optional[str] = None,
             token_file: Optional[str] = None) -> Endpoint:
    """Locate the host without configuration. Raises SkizzyConnectionError (NO_ENDPOINT / NO_TOKEN)."""
    tried: List[str] = []
    plugin_data = None
    if endpoint_file:
        plugin_data = os.path.dirname(os.path.abspath(endpoint_file))
    elif ksp:
        plugin_data = plugin_data_for(ksp)
    else:
        env_ep = os.environ.get("SKIZZY_ENDPOINT")
        env_ksp = os.environ.get("KSPDIR")
        if env_ep:
            plugin_data = os.path.dirname(os.path.abspath(env_ep))
        elif env_ksp:
            plugin_data = plugin_data_for(env_ksp)
        else:
            ptr = user_pointer_path()
            if ptr:
                tried.append(ptr)
                if os.path.isfile(ptr):
                    try:
                        plugin_data = _read_json(ptr).get("pluginData")
                    except (OSError, ValueError):
                        plugin_data = None
    if not plugin_data:
        raise SkizzyConnectionError(
            "NO_ENDPOINT",
            "could not find a running skizzy host (looked in: %s)" % ("; ".join(tried) or "SKIZZY_ENDPOINT, KSPDIR, user pointer"),
            hint="Start KSP with skizzy-protocol installed, or pass ksp=<KSP folder> (or set KSPDIR).")
    return from_plugin_data(plugin_data, token_file)
