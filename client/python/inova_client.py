"""Async Python client for the Inova-API-Plugin.

Covers all current endpoints — HTTP GETs plus the /state/stream WebSocket.
Designed as a starting point for data-collection scripts and notebooks.

Quick start:

    import asyncio
    from inova_client import InovaClient

    async def main():
        async with InovaClient() as client:
            print(await client.ping())
            info = await client.info()
            print(f"plugin {info['plugin']} v{info['version']}")

    asyncio.run(main())

Override the base URL via constructor arg or the INOVA_API_BASE_URL env var.
"""

from __future__ import annotations

import json
import os
from typing import AsyncIterator

import httpx
import websockets


DEFAULT_BASE_URL = os.environ.get("INOVA_API_BASE_URL", "http://192.168.1.146:5001")


class InovaClient:
    """Async client for the Inova API plugin."""

    def __init__(self, base_url: str = DEFAULT_BASE_URL, timeout: float = 5.0):
        self.base_url = base_url.rstrip("/")
        self._http = httpx.AsyncClient(timeout=timeout)

    async def __aenter__(self) -> "InovaClient":
        return self

    async def __aexit__(self, *exc) -> None:
        await self.close()

    async def close(self) -> None:
        await self._http.aclose()

    # ---- one-shot endpoints ----

    async def ping(self) -> str:
        r = await self._http.get(f"{self.base_url}/ping")
        r.raise_for_status()
        return r.text

    async def info(self) -> dict:
        return await self._get_json("/info")

    async def movement_position(self) -> dict:
        return await self._get_json("/movement/position")

    async def lights_state(self) -> dict:
        return await self._get_json("/lights/state")

    async def power_current(self) -> dict:
        return await self._get_json("/power/current")

    async def temperature_current(self) -> dict:
        return await self._get_json("/temperature/current")

    async def state_snapshot(self) -> dict:
        return await self._get_json("/state/snapshot")

    # ---- live stream ----

    async def stream_state(self, hz: int = 10) -> AsyncIterator[dict]:
        """Yield decoded JSON frames from /state/stream until cancelled or disconnected.

        Each frame is `{ "respondedAt": str, "data": { position, lights, power, temperature } }`.
        """
        ws_url = self.base_url.replace("http://", "ws://", 1).replace("https://", "wss://", 1)
        async with websockets.connect(f"{ws_url}/state/stream?hz={hz}") as ws:
            async for raw in ws:
                yield json.loads(raw)

    # ---- internals ----

    async def _get_json(self, path: str) -> dict:
        r = await self._http.get(f"{self.base_url}{path}")
        r.raise_for_status()
        return r.json()
