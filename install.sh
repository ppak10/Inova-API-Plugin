#!/usr/bin/env bash
# install.sh — install/update the Inova-API-Plugin on the printer.
#
# Run on the printer (e.g. inova). Idempotent: safe to re-run after `git pull`.
#
#   1. Copies dist/Inova.ApiPlugin.dll into ~/SLS4All/Plugins/Inova.ApiPlugin/
#   2. Adds/refreshes plugin entries in ~/SLS4All/Configuration/appsettings.user.toml
#      (delimited by a marker block so re-runs don't duplicate)
#
# Does NOT restart the SLS4All service — the firmware must be restarted
# manually for changes to take effect.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Overridable via env if the layout differs
SLS4ALL_HOME="${SLS4ALL_HOME:-${HOME}/SLS4All}"
PLUGIN_NAME="Inova.ApiPlugin"
PLUGIN_TYPE="Inova.ApiPlugin.InovaApiPlugin"

PLUGIN_DIR="${SLS4ALL_HOME}/Plugins/${PLUGIN_NAME}"
USER_CONFIG="${SLS4ALL_HOME}/Configuration/appsettings.user.toml"
DLL_SRC="${SCRIPT_DIR}/dist/${PLUGIN_NAME}.dll"
DLL_DST="${PLUGIN_DIR}/${PLUGIN_NAME}.dll"

MARKER_BEGIN="# >>> ${PLUGIN_NAME} (managed by install.sh) >>>"
MARKER_END="# <<< ${PLUGIN_NAME} (managed by install.sh) <<<"

# --- Sanity ---
if [[ ! -f "${DLL_SRC}" ]]; then
    echo "ERROR: ${DLL_SRC} not found. Run build.sh on the dev machine and commit dist/." >&2
    exit 1
fi
if [[ ! -d "${SLS4ALL_HOME}" ]]; then
    echo "ERROR: ${SLS4ALL_HOME} not found. Are you running this on the printer?" >&2
    exit 1
fi
if [[ ! -d "$(dirname "${USER_CONFIG}")" ]]; then
    echo "ERROR: $(dirname "${USER_CONFIG}") not found." >&2
    exit 1
fi

# --- 1. Copy DLL ---
mkdir -p "${PLUGIN_DIR}"
cp -f "${DLL_SRC}" "${DLL_DST}"
echo "Installed: ${DLL_DST}"

# --- 2. Update appsettings.user.toml ---
# Strip any prior marker block, then append a fresh one. Tolerant of missing file.
TMP_CONFIG="$(mktemp)"
trap 'rm -f "${TMP_CONFIG}"' EXIT

if [[ -f "${USER_CONFIG}" ]]; then
    # Drop anything between (and including) our markers
    sed "/^${MARKER_BEGIN}$/,/^${MARKER_END}$/d" "${USER_CONFIG}" > "${TMP_CONFIG}"
else
    : > "${TMP_CONFIG}"
fi

# Ensure trailing newline before appending
if [[ -s "${TMP_CONFIG}" ]] && [[ "$(tail -c1 "${TMP_CONFIG}")" != $'\n' ]]; then
    echo "" >> "${TMP_CONFIG}"
fi

cat >> "${TMP_CONFIG}" <<EOF
${MARKER_BEGIN}
# Keys must be quoted because they contain dots — unquoted dotted keys
# in TOML are interpreted as nested tables, not literal single-key names.
[Application.PluginAssemblies]
"${PLUGIN_NAME}" = "${DLL_DST}"

[Application.PluginServices."${PLUGIN_NAME}"]
Implementation = "${PLUGIN_TYPE}, ${PLUGIN_NAME}"
Registration = "AsImplementationAndInterfaces"
Lifetime = "Singleton"

# Substitute the firmware's closed-source ImageCodePlotter with our
# LoggingCodePlotter decorator. The decorator forwards every ICodePlotter call
# to the original (constructed reflectively at startup) and additionally tees
# Process(CodeCommand) into a fan-out + per-layer ring buffer, exposed via the
# /plotter/commands/stream WS and /plotter/layer/{n}/commands GET.
# See CompactServiceCollectionExtensions.cs:115 for how this is applied.
[Application.PluginReplacements."Inova.LoggingCodePlotter"]
Original = "SLS4All.Compact.Slicing.ImageCodePlotter, SLS4All.Compact.Processing"
Replacement = "Inova.ApiPlugin.LoggingCodePlotter, ${PLUGIN_NAME}"

# Subclass McuMovementClient to intercept MoveXY/SetLaser. This is where
# per-vector galvo data is actually visible — the slicer goes through
# IMovementClient for hardware MCU access, but bypasses the DI ICodePlotter
# for per-command writes. The intercepted frames flow into LoggingCodePlotter's
# fan-out (so the same /plotter/commands/stream WS receives them).
[Application.PluginReplacements."Inova.LoggingMovementClient"]
Original = "SLS4All.Compact.Movement.McuMovementClient, SLS4All.Compact.McuClient"
Replacement = "Inova.ApiPlugin.LoggingMovementClient, ${PLUGIN_NAME}"
${MARKER_END}
EOF

mv "${TMP_CONFIG}" "${USER_CONFIG}"
trap - EXIT
echo "Updated:   ${USER_CONFIG}"

echo ""

# --- 3. Probe for ImageCodePlotter assembly ---
# The PluginReplacements config above pins SLS4All.Compact.Slicing.ImageCodePlotter
# in SLS4All.Compact.Slicing.dll. If that assembly name drifts in a future
# firmware release the decorator's AppDomain-scan fallback should still find it,
# but flagging mismatches here at install time is cheaper than debugging at boot.
PROBE_DIRS=(
    "${SLS4ALL_HOME}/Bin"
    "${SLS4ALL_HOME}/Current"
    "${SLS4ALL_HOME}/Current/SLS4All.Compact.PrinterApp"
)
PROBE_HITS=""
for dir in "${PROBE_DIRS[@]}"; do
    if [[ -d "${dir}" ]]; then
        hits=$(grep -l -r --include="*.dll" "ImageCodePlotter" "${dir}" 2>/dev/null | head -5 || true)
        if [[ -n "${hits}" ]]; then
            PROBE_HITS="${PROBE_HITS}${hits}\n"
        fi
    fi
done

if [[ -n "${PROBE_HITS}" ]]; then
    echo "ImageCodePlotter candidates (verify the AQN in the [PluginReplacements] block above):"
    printf "  %s\n" $(echo -e "${PROBE_HITS}")
else
    echo "WARNING: could not locate ImageCodePlotter in any standard firmware directory."
    echo "         If the firmware fails to load LoggingCodePlotter at startup,"
    echo "         check ~/SLS4All/Current/logs/default*.log for AQN resolution errors."
fi

echo ""
echo "Plugin installed. Restart the SLS4All service for changes to take effect."
