"""skizzy: Python sync client for skizzy-protocol (control protocol for Kerbal Space Program)."""
__version__ = "0.1.0"

from .errors import SkizzyConnectionError, SkizzyError  # noqa: E402
from .client import Client, Subscription, connect  # noqa: E402

__all__ = ["connect", "Client", "Subscription", "SkizzyError", "SkizzyConnectionError", "__version__"]
