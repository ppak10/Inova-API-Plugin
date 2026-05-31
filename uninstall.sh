#!/usr/bin/env bash
# uninstall.sh — remove the Inova-API-Plugin from the printer.
#
# Run on the printer (e.g. inova). Idempotent: safe to re-run.
#
#   1. Strips the plugin's marker block from
#      ~/SLS4All/Configuration/appsettings.user.toml
#   2. Removes ~/SLS4All/Plugins/Inova.ApiPlugin/
#
# Does NOT restart the SLS4All service — the firmware must be restarted
# manually for changes to take effect.

set -euo pipefail

SLS4ALL_HOME="${SLS4ALL_HOME:-${HOME}/SLS4All}"
PLUGIN_NAME="Inova.ApiPlugin"

PLUGIN_DIR="${SLS4ALL_HOME}/Plugins/${PLUGIN_NAME}"
USER_CONFIG="${SLS4ALL_HOME}/Configuration/appsettings.user.toml"

MARKER_BEGIN="# >>> ${PLUGIN_NAME} (managed by install.sh) >>>"
MARKER_END="# <<< ${PLUGIN_NAME} (managed by install.sh) <<<"

# --- 1. Strip marker block from appsettings.user.toml ---
if [[ -f "${USER_CONFIG}" ]]; then
    if grep -qF "${MARKER_BEGIN}" "${USER_CONFIG}"; then
        TMP_CONFIG="$(mktemp)"
        trap 'rm -f "${TMP_CONFIG}"' EXIT
        sed "/^${MARKER_BEGIN}$/,/^${MARKER_END}$/d" "${USER_CONFIG}" > "${TMP_CONFIG}"
        mv "${TMP_CONFIG}" "${USER_CONFIG}"
        trap - EXIT
        echo "Cleaned:   ${USER_CONFIG}"
    else
        echo "Skipped:   ${USER_CONFIG} (no marker block found)"
    fi
else
    echo "Skipped:   ${USER_CONFIG} (not present)"
fi

# --- 2. Remove plugin directory ---
if [[ -d "${PLUGIN_DIR}" ]]; then
    rm -rf "${PLUGIN_DIR}"
    echo "Removed:   ${PLUGIN_DIR}"
else
    echo "Skipped:   ${PLUGIN_DIR} (not present)"
fi

echo ""
echo "Plugin uninstalled. Restart the SLS4All service for changes to take effect."
