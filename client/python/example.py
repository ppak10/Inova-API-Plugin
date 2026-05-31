"""Runnable demo of the Inova client.

Run with uv (no global install needed):

    cd client/python
    uv run example.py

Or with a regular venv:

    pip install httpx websockets
    python example.py
"""

import asyncio

from inova_client import InovaClient


async def main() -> None:
    async with InovaClient() as client:
        # One-shot endpoints
        print("ping:", await client.ping())

        info = await client.info()
        print(f"plugin: {info['plugin']} v{info['version']} "
              f"(uptime {info['uptimeSeconds']:.1f}s)")

        pos = (await client.movement_position())["data"]
        print(f"position: x={pos['x']} y={pos['y']} z1={pos['z1']:.2f} "
              f"z2={pos['z2']} r={pos['r']}")

        lights = (await client.lights_state())["data"]
        print(f"lights: enabled={lights['isEnabled']} count={lights['lightCount']}")

        snapshot = await client.state_snapshot()
        n_temps = len(snapshot["data"]["temperature"]["entries"])
        n_power = len(snapshot["data"]["power"]["entries"])
        print(f"snapshot: {n_temps} temp sensors, {n_power} power channels")

        # Live stream — take 5 frames at 4 Hz and exit
        print("\nstreaming /state/stream at 4 Hz (5 frames)...")
        count = 0
        async for frame in client.stream_state(hz=4):
            pos = frame["data"]["position"]
            print(f"  [{frame['respondedAt'][11:23]}] "
                  f"x={pos['x']} y={pos['y']} z1={pos['z1']:.2f}")
            count += 1
            if count >= 5:
                break


if __name__ == "__main__":
    asyncio.run(main())
