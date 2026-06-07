# Inova-API-Plugin

A plugin for the [SLS4All Compact](https://sls4all.com/) firmware on the Inova MK1 (Raspberry Pi 5) that exposes the printer's services over an HTTP + WebSocket API on a side port. Loads into `SLS4All.Compact.PrinterApp` via the firmware's plugin loader, serves on port **5001**, leaves the firmware's own Blazor UI untouched on its usual port.

## API surface

| Endpoint | Returns |
| --- | --- |
| `GET /ping` | `"pong"` |
| `GET /info` | plugin metadata + uptime |
| `GET /movement/position` | live galvo `{ x, y, z1, z2, r }` |
| `GET /lights/state` | `{ isEnabled, lightCount }` |
| `GET /power/current` | per-output power + powerman state |
| `GET /temperature/current` | per-sensor temperature entries (no IR matrix) |
| `GET /temperature/bedmatrix` | raw IR thermal pixel matrix: `{ timestamp, width, height, values: float[] }`. `data` is `null` if no camera or no frame yet. |
| `WS /temperature/bedmatrix/stream` | event-driven matrix stream from `StateChangedHighFrequency` (~6 Hz native). `?hz=N` decimates (1–60). Frames with no matrix are skipped. |
| `GET /plotter/info` | quick metadata about the current-layer galvo exposure mask: `{ width, height, version, layerCount, isEmpty }`. `version` increments as the plotter receives commands; cheap to poll. |
| `GET /plotter/mask` | raw exposure mask from `ICodePlotter`: `{ width, height, version, values: float[] }`. This is the live galvo trace — `CurrentPosition.X/Y` does NOT track raster moves, but the plotter accumulates them. |
| `GET /state/snapshot` | combined snapshot of all telemetry |
| `WS /state/stream?hz=N` | periodic snapshots, 1–100 Hz (default 100) |
| `WS /movement/position/stream` | event-driven position stream from `PositionChangedHighFrequency` (~1 kHz native). `?hz=N` decimates to ≤N sends/sec (1–1000). Frame shape: `{ respondedAt, data: { x, y, z1, z2, r, hasHomed } }`. |

All data endpoints wrap their payload in `{ respondedAt, data: ... }`. Combined with the per-sample `elapsedFromNow` field where the firmware provides it, clients can reconstruct sub-second wall-clock time for each underlying reading.

Sample clients in `client/` — Python (`httpx` + `websockets`) and TypeScript (`ws`).

## Repository layout

| Path | Purpose |
| --- | --- |
| `Inova.ApiPlugin.csproj` | .NET 10 project. References `SLS4All.Compact` + `SLS4All.Compact.Core`. |
| `InovaApiPlugin.cs` | Plugin entry. `IHostedService` + `IConstructable`. Hosts Kestrel via `WebApplication.CreateSlimBuilder` and forwards firmware services into the child DI container. |
| `build.sh` | Dev-side: `dotnet build -c Release`, stages the DLL into `dist/`. |
| `install.sh` | Printer-side: copies the DLL into `~/SLS4All/Plugins/Inova.ApiPlugin/` and idempotently edits `~/SLS4All/Configuration/appsettings.user.toml`. |
| `uninstall.sh` | Inverse of `install.sh`. |
| `dist/Inova.ApiPlugin.dll` | Committed prebuilt — printer can install without a .NET SDK. |
| `client/python/`, `client/typescript/` | Reference clients with runnable examples. |

## How it works

The firmware loads our DLL via `Assembly.LoadFrom` (configured under `[Application.PluginAssemblies]`) and registers our entry class as a singleton exposed as all its interfaces (configured under `[Application.PluginServices.*]`). Because the class implements `IHostedService`, the host calls `StartAsync` on us; because it also implements `IConstructable`, the firmware constructs it eagerly at startup rather than lazily on first use.

`StartAsync` spins up a child `WebApplication` with Kestrel on port 5001 and forwards selected firmware singletons (`IMovementClient`, `ITemperatureClient`, `IPowerClient`, `ILightsClient`) into the child DI container. The forwarded services are the **same instances** the firmware uses internally — both containers reference one shared object per type, so handler reads and method calls land on the live state. See the `Forward<T>` helper in `InovaApiPlugin.cs`.

## Build

```bash
./build.sh
```

Requirements:

- .NET 10 SDK matching the deployed runtime (verified at 10.0.8).
- The [SLS4All.Compact](https://github.com/sls4all/SLS4All.Compact) firmware source at `../SLS4All.Compact/`. When this repo is used as a submodule of a parent that also includes SLS4All.Compact side-by-side, `git submodule update --init --recursive` on the parent satisfies the layout. When cloning standalone, place SLS4All.Compact next to it manually.

Notes:

- The plugin does **not** reference `SLS4All.Compact.AppCore` because AppCore transitively depends on private NuGet packages from the SLS4All GitHub Packages feed. Endpoints that need types from AppCore would require either resolving the auth or referencing the deployed DLLs directly. `Core` has been enough so far.
- The output DLL is architecture-neutral managed IL — same file runs on `linux-arm64` (the printer) and any other .NET 10 host.

## Install on the printer

```bash
cd ~/GitHub
git clone <repo-url> Inova-API-Plugin
cd Inova-API-Plugin
./install.sh        # copies DLL, edits appsettings.user.toml
# then restart the firmware (see below)
```

`install.sh` is idempotent — re-running after `git pull` cleanly replaces the previous deploy. It does **not** restart the firmware; changes take effect on next start.

The TOML edit is marker-bracketed:

```toml
# >>> Inova.ApiPlugin (managed by install.sh) >>>
[Application.PluginAssemblies]
"Inova.ApiPlugin" = "/home/<user>/SLS4All/Plugins/Inova.ApiPlugin/Inova.ApiPlugin.dll"

[Application.PluginServices."Inova.ApiPlugin"]
Implementation = "Inova.ApiPlugin.InovaApiPlugin, Inova.ApiPlugin"
Registration = "AsImplementationAndInterfaces"
Lifetime = "Singleton"
# <<< Inova.ApiPlugin (managed by install.sh) <<<
```

Keys are quoted because TOML treats unquoted dotted keys as nested tables — `Inova.ApiPlugin` would mean a sub-table, not a literal name.

Override `SLS4ALL_HOME` (default `${HOME}/SLS4All`) if your layout differs:

```bash
SLS4ALL_HOME=/some/other/path ./install.sh
```

## Restart the firmware

No systemd unit on this printer — the launcher is `~/sls4all_run.sh`, kicked off by an XDG autostart at desktop login. The simplest restart is `sudo reboot`. To soft-restart without rebooting:

```bash
pkill -f sls4all_run.sh
nohup ~/sls4all_run.sh -s >/dev/null 2>&1 &
```

Verify:

```bash
curl http://192.168.1.146:5001/ping        # → pong
```

On failure, check `~/SLS4All/Current/logs/default*.log` for plugin-loading errors. Look for lines mentioning `Inova.ApiPlugin` or `Assembly.LoadFrom`.

## Uninstall

```bash
cd ~/GitHub/Inova-API-Plugin
./uninstall.sh
# then restart the firmware
```

Idempotent. Reports `Skipped:` for anything already absent. If the repo clone isn't available, the same operations by hand:

```bash
sed -i '/^# >>> Inova.ApiPlugin/,/^# <<< Inova.ApiPlugin/d' ~/SLS4All/Configuration/appsettings.user.toml
rm -rf ~/SLS4All/Plugins/Inova.ApiPlugin
```

## Design notes

- **Own port (5001), not the firmware's port.** Adding our endpoints to the firmware's Kestrel would require patching `StartupBase.cs` to call `AddApplicationPart` for plugin assemblies. Running our own child host on a side port avoids the patch entirely and keeps this plugin a zero-firmware-fork solution.
- **Kestrel + minimal APIs, via `WebApplication.CreateSlimBuilder`.** `Microsoft.AspNetCore.App` is already in the firmware's runtime, so no extra dependencies. Routing, JSON, WebSocket support, and DI per handler all come for free.
- **No auth.** Matches the firmware's effective stance on this unit — `[Authorize]` is declared on its controllers but enforcement is disabled. Wide open on the LAN. Add auth before any non-trusted-network exposure.
- **WebSocket subprotocol gotcha.** The parameterless `ctx.WebSockets.AcceptWebSocketAsync()` overload on ASP.NET Core 10 silently sets `SubProtocol = "default"`, causing the 101 response to carry `Sec-WebSocket-Protocol: default` — which RFC 6455 forbids when the client didn't offer any subprotocol. Strict WS clients (Python `websockets`) reject the connection. Fix is in code: pass `new WebSocketAcceptContext { SubProtocol = null }` explicitly.

## Related

- [SLS4All.Compact](https://github.com/sls4all/SLS4All.Compact) — the open-source firmware this plugin extends.
- The bundled `SLS4All.Compact.TestPlugin/` in that repo shows the plugin-loader pattern.
