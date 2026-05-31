# Inova-API-Plugin

A plugin for the [SLS4All Compact](https://sls4all.com/) firmware running on the Inova MK1 (Raspberry Pi 5) that exposes the printer's services over an HTTP API on a side port. The aim is a clean control + telemetry surface that external tools (Python scripts, a custom React frontend, research notebooks) can use to drive prints, run tests, and capture data without modifying the core firmware.

The plugin loads alongside `SLS4All.Compact.PrinterApp` via the firmware's built-in plugin loader (`[Application.PluginAssemblies]`), runs as an `IHostedService`, and serves HTTP on **port 5001** while the firmware's Blazor UI continues on its usual port.

## Status

MVP. The plugin currently exposes one endpoint (`GET /ping`) used to validate the toolchain end-to-end (build, deploy, plugin load, port reachable). Real control + telemetry endpoints will be added on top of this plumbing.

## Repository layout

| Path | Purpose |
| --- | --- |
| `Inova.ApiPlugin.csproj` | .NET project targeting `net10.0`. References the SLS4All.Compact source clone (see Build requirements). |
| `InovaApiPlugin.cs` | Plugin entry point. Implements `IHostedService` (start/stop lifecycle) and `IConstructable` (eager construction by the firmware). Opens an `HttpListener` on port 5001. |
| `build.sh` | Dev-side: runs `dotnet build -c Release` and stages the resulting DLL into `dist/` for commit. |
| `install.sh` | Printer-side: copies `dist/Inova.ApiPlugin.dll` into `~/SLS4All/Plugins/Inova.ApiPlugin/` and idempotently edits `~/SLS4All/Configuration/appsettings.user.toml`. |
| `uninstall.sh` | Printer-side: removes the plugin DLL and strips the marker block from `appsettings.user.toml`. Inverse of `install.sh`. |
| `dist/Inova.ApiPlugin.dll` | Prebuilt plugin binary, committed so the printer can install without a .NET SDK. |

## How it works

The SLS4All firmware loads plugin assemblies named in `[Application.PluginAssemblies]` via `Assembly.LoadFrom`, registers types from `[Application.PluginServices.*]` with the DI container, and the ASP.NET host then starts any registered `IHostedService` automatically. Our entry class implements both `IHostedService` and `IConstructable`:

- `IConstructable` causes the firmware to eagerly construct the plugin at startup rather than lazily on first use.
- `IHostedService.StartAsync` is called by the host once construction completes; this is where we start the `HttpListener` accept loop on port 5001.
- `IHostedService.StopAsync` is called on shutdown; we stop the listener cleanly.

The plugin loads into the same process as `SLS4All.Compact.PrinterApp` and shares its DI container, so any injected services (e.g., `ILogger`, and later `IPrintingService`, `IMovementClient`, etc.) come from the live printer state.

## Build (dev machine)

### Requirements

- .NET 10 SDK. The deployed firmware runs on .NET 10.0.8; matching the major version is required.
- The [SLS4All.Compact](https://github.com/sls4all/SLS4All.Compact) firmware source at `../SLS4All.Compact/` (sibling directory). The `.csproj` references it for the `SLS4All.Compact` and `SLS4All.Compact.Core` projects.

  When this repo is used as a submodule of a parent that includes both repos side-by-side (e.g. `<parent>/sls4all/Inova-API-Plugin/` and `<parent>/sls4all/SLS4All.Compact/`), running `git submodule update --init --recursive` on the parent satisfies the layout automatically.

  When cloning this repo standalone, place SLS4All.Compact next to it manually:

  ```bash
  cd ..
  git clone https://github.com/sls4all/SLS4All.Compact.git
  ```

### Steps

```bash
./build.sh
```

`build.sh` runs `dotnet build -c Release` and copies the output DLL into `dist/`. Commit `dist/Inova.ApiPlugin.dll` so the printer can install without a .NET SDK of its own.

### Notes

- The plugin does **not** reference `SLS4All.Compact.AppCore` because AppCore transitively depends on private NuGet packages (`SLS4All.Compact.Slicing`, `SLS4All.Compact.Nesting`, `SLS4All.Compact.Processing`) hosted on a private GitHub Packages feed that we don't have credentials for. Adding endpoints that need types from AppCore will require resolving this — likely by switching to direct `<Reference>` against the deployed DLLs on the printer.
- The plugin DLL is architecture-neutral managed IL. The same `.dll` runs on `linux-arm64` (the printer) and any other .NET 10 host, so no `-r linux-arm64` is needed.
- Build output is ~10 KB.

## Install (on the printer)

### Requirements

- The plugin repo cloned somewhere readable on the printer (e.g., `~/GitHub/Inova-API-Plugin/`).
- The SLS4All firmware deployed at `~/SLS4All/Current/` with a writable `~/SLS4All/Configuration/` directory (default layout).

### Steps

```bash
cd ~/GitHub
git clone <repo-url> Inova-API-Plugin
cd Inova-API-Plugin
./install.sh
```

`install.sh` performs two actions:

1. Copies `dist/Inova.ApiPlugin.dll` into `~/SLS4All/Plugins/Inova.ApiPlugin/`.
2. Edits `~/SLS4All/Configuration/appsettings.user.toml` to add:

   ```toml
   # >>> Inova.ApiPlugin (managed by install.sh) >>>
   [Application.PluginAssemblies]
   Inova.ApiPlugin = "/home/<user>/SLS4All/Plugins/Inova.ApiPlugin/Inova.ApiPlugin.dll"

   [Application.PluginServices.Inova.ApiPlugin]
   Implementation = "Inova.ApiPlugin.InovaApiPlugin, Inova.ApiPlugin"
   Registration = "AsImplementationAndInterfaces"
   Lifetime = "Singleton"
   # <<< Inova.ApiPlugin (managed by install.sh) <<<
   ```

The TOML edit is bracketed by markers so re-runs after `git pull` cleanly replace the block instead of appending duplicates.

The install script does **not** restart the SLS4All service. Changes take effect on the next restart.

### Overrides

`SLS4ALL_HOME` defaults to `${HOME}/SLS4All`. Override via env if your layout differs:

```bash
SLS4ALL_HOME=/some/other/path ./install.sh
```

## Restart the firmware

There is no systemd unit for SLS4All on the deployed printer. The launcher is a plain shell loop in `~/sls4all_run.sh` that re-launches `Current/sls4all_run_Inova_RaspberryPI5.sh` whenever it exits with code 5. To restart the running firmware, kill the process tree and re-launch:

```bash
# Find the launcher PID (look for sls4all_run.sh at the top of the tree)
pgrep -f sls4all_run.sh
# Kill it (and the children will follow)
pkill -f sls4all_run.sh
# Re-launch in the background — your environment may already do this on login
nohup ~/sls4all_run.sh -s >/dev/null 2>&1 &
```

Restarting takes a few seconds for the .NET host to come up. The plugin should be loaded by the time the Blazor UI is reachable.

## Verify

Once the firmware is running with the plugin loaded, from any host on the same network:

```bash
curl http://192.168.1.146:5001/ping
# expected output: pong
```

If you get a connection error, check the firmware log at `~/SLS4All/Current/logs/default*.log` for plugin loading errors. Look for lines mentioning `Inova.ApiPlugin` or `Assembly.LoadFrom`.

## Uninstall

From the clone on the printer:

```bash
cd ~/GitHub/Inova-API-Plugin
./uninstall.sh
# then restart the firmware (see above)
```

`uninstall.sh` is the inverse of `install.sh`. It strips the plugin's marker block from `~/SLS4All/Configuration/appsettings.user.toml` and removes `~/SLS4All/Plugins/Inova.ApiPlugin/`. It is idempotent — safe to re-run, and reports `Skipped:` for anything already absent.

If the repo clone is no longer available on the printer (e.g. the user deleted it), the same two operations can be performed by hand:

```bash
sed -i '/^# >>> Inova.ApiPlugin/,/^# <<< Inova.ApiPlugin/d' \
    ~/SLS4All/Configuration/appsettings.user.toml
rm -rf ~/SLS4All/Plugins/Inova.ApiPlugin
```

Neither variant restarts the firmware — see the Restart section above.

## Development workflow

1. Edit `InovaApiPlugin.cs` (or add new files in the repo root).
2. `./build.sh` — rebuilds and refreshes `dist/Inova.ApiPlugin.dll`.
3. `git commit -am "..."` and `git push`.
4. On the printer: `cd ~/GitHub/Inova-API-Plugin && git pull && ./install.sh`.
5. Restart the firmware (see above).
6. Verify with `curl`.

Iteration is currently ~30 seconds end-to-end after the initial setup.

## Design notes

- **Own port (5001), not the firmware's port (5000).** The firmware's Kestrel only auto-discovers MVC controllers from the AppCore assembly. Contributing controllers from a plugin assembly would require either patching `StartupBase.cs` to call `AddApplicationPart` for plugin assemblies, or working around the auto-discovery somehow. Running our own `HttpListener` on a side port avoids this entirely and keeps the plugin a zero-firmware-fork solution.
- **`HttpListener` for MVP, not Kestrel.** A single endpoint with no JSON, routing, or auth doesn't justify standing up a parallel `WebApplication` host inside our `IHostedService`. Once we start adding real endpoints we'll likely switch to Kestrel / Minimal APIs for clean routing and content negotiation.
- **No auth.** The deployed firmware has `[Authorize]` on its own controllers but auth is effectively disabled on this unit (we verified by curling `/api/status` unauthenticated). The plugin matches that — wide open on the LAN. Plan to add auth before exposing this to anything beyond a trusted network.

## Related

- [SLS4All.Compact](https://github.com/sls4all/SLS4All.Compact) — the open-source firmware this plugin extends.
- The bundled `SLS4All.Compact.TestPlugin/` in that repo is the reference implementation showing the plugin loader pattern (`IDelayedConstructable`, `[Application.PluginAssemblies]`, options binding).
