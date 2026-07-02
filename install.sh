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
    # Drop anything between (and including) our markers, AND normalize CRLF→LF
    # in the preserved prefix. The firmware's bundled Tomlet trips on mixed
    # line endings even when the content is otherwise valid (the SLS4All
    # OptionsClassName template at the top of this file is shipped with CRLF
    # on some firmware builds; our heredoc below appends LF — without this
    # normalization the file ends up mixed, parses fine in most TOML libs,
    # but reliably fails the firmware's parser).
    sed "/^${MARKER_BEGIN}$/,/^${MARKER_END}$/d" "${USER_CONFIG}" \
        | sed 's/\r$//' > "${TMP_CONFIG}"
else
    : > "${TMP_CONFIG}"
fi

# Ensure trailing newline before appending
if [[ -s "${TMP_CONFIG}" ]] && [[ "$(tail -c1 "${TMP_CONFIG}")" != $'\n' ]]; then
    echo "" >> "${TMP_CONFIG}"
fi

# Inline-table values under bare-segment table headers, NOT dotted-quoted
# segments in table headers. The firmware's Tomlet has a bug: two sibling
# headers like [Parent."Quoted.A"] / [Parent."Quoted.B"] cause it to split on
# the dot INSIDE the quotes when navigating to the existing Parent, throwing
# TomlNoSuchValueException for the partial-key `"Quoted`. The bare keys
# `"Quoted.A" = { ... }` form below avoids that codepath entirely — the
# parser handles quoted dotted bare keys fine, only table headers are buggy.
cat >> "${TMP_CONFIG}" <<EOF
${MARKER_BEGIN}
[Application.PluginAssemblies]
"${PLUGIN_NAME}" = "${DLL_DST}"

[Application.PluginServices]
"${PLUGIN_NAME}" = { Implementation = "${PLUGIN_TYPE}, ${PLUGIN_NAME}", Registration = "AsImplementationAndInterfaces", Lifetime = "Singleton" }

# Plugin replacements:
#   Inova.LoggingCodePlotter: decorates the firmware's closed-source
#     ImageCodePlotter so every ICodePlotter call is teed into a fan-out +
#     per-layer ring buffer (exposed via /plotter/commands/stream WS and
#     /plotter/layer/{n}/commands GET).
#   Inova.LoggingMovementClient (DISABLED 2026-06-29): subclasses
#     McuMovementClient. First real deployment hung the firmware at
#     "Creating delayed constructables" phase with 0% CPU and no plugin
#     trace — strongly suspected DI cycle (LoggingMovementClient depends on
#     McuPrinterClient, which depends on McuMovementClient, which the
#     substitution rewrites back to LoggingMovementClient). The plugin DLL
#     still ships the class for future re-enablement once the cycle is broken
#     (e.g. via Lazy<McuPrinterClient> or a different registration shape);
#     leave the TOML line commented out until then.
#   Inova.FullRecoatLayerClient: subclasses LayerClient to expand one layer
#     into N complete recoats at 1/N thickness (the /printing/recoater-passes-full
#     override). Passthrough (single base call) while the override is unset.
#     DI-cycle note: unlike LoggingMovementClient, LayerClient's dependencies
#     (movement/printer/temperature/surface clients) don't resolve ILayerClient
#     back, so the substitution shouldn't recreate that boot hang — but watch
#     the first boot after enabling regardless.
# See CompactServiceCollectionExtensions.cs:115 for how the replacement
# mechanism is applied.
[Application.PluginReplacements]
"Inova.LoggingCodePlotter" = { Original = "SLS4All.Compact.Slicing.ImageCodePlotter, SLS4All.Compact.Processing", Replacement = "Inova.ApiPlugin.LoggingCodePlotter, ${PLUGIN_NAME}" }
"Inova.FullRecoatLayerClient" = { Original = "SLS4All.Compact.Movement.LayerClient, SLS4All.Compact.Processing", Replacement = "Inova.ApiPlugin.FullRecoatLayerClient, ${PLUGIN_NAME}" }
# "Inova.LoggingMovementClient" = { Original = "SLS4All.Compact.Movement.McuMovementClient, SLS4All.Compact.McuClient", Replacement = "Inova.ApiPlugin.LoggingMovementClient, ${PLUGIN_NAME}" }
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
