#!/bin/bash
#
# RemoteRelay updater — fetches the latest release from GitHub and installs it
# over the current installation. Usually invoked via `sudo remoterelay update`.
#
#   sudo ./update.sh                 update to the latest stable release
#   sudo ./update.sh --pre-release   update to the latest pre-release
#   sudo ./update.sh --force         reinstall even if already up to date
#
set -uo pipefail

UPDATE_SCRIPT_VERSION="2.0.0"

# --- locate + load shared libraries ------------------------------------------
_self_dir="$(cd "$(dirname "$0")" && pwd)"
RR_LIB=""
for d in /usr/local/lib/remoterelay "$_self_dir/lib" "$_self_dir"; do
  if [ -f "$d/rr-ui.sh" ] && [ -f "$d/rr-common.sh" ]; then RR_LIB="$d"; break; fi
done
if [ -n "$RR_LIB" ]; then
  # shellcheck source=lib/rr-ui.sh
  . "$RR_LIB/rr-ui.sh"
  # shellcheck source=lib/rr-common.sh
  . "$RR_LIB/rr-common.sh"
else
  echo "Error: rr-ui.sh / rr-common.sh not found." >&2
  exit 1
fi
RR_BACKTITLE="RemoteRelay updater"

ui_header "RemoteRelay updater v${UPDATE_SCRIPT_VERSION}"

# --- args ---------------------------------------------------------------------
PRE_RELEASE=false
FORCE=false
INSTALLER_ARGS=()
# Default channel comes from how the system was installed.
RR_CONF_CHANNEL="stable"
while [ "$#" -gt 0 ]; do
  case "$1" in
    -p|--pre-release) PRE_RELEASE=true ;;
    -f|--force)       FORCE=true ;;
    --unattended)     export RR_UNATTENDED=1; INSTALLER_ARGS+=("--unattended") ;;
    *) ui_warn "Unknown parameter ignored: $1" ;;
  esac
  shift
done
[ "${RR_UNATTENDED:-0}" = 1 ] && RR_INTERACTIVE=0

if [ "$(id -u)" -ne 0 ]; then
  ui_error "This script must be run as root (sudo)."
  exit 1
fi

# --- resolve installation -----------------------------------------------------
rr_read_install_conf || true
if ! rr_resolve_user; then exit 1; fi
rr_set_paths
[ "${RR_CONF_CHANNEL:-stable}" = "pre" ] && PRE_RELEASE=true
$PRE_RELEASE && INSTALLER_ARGS+=("--pre-release")

BACKUP_DIR="$USER_HOME/.remoterelay-backups"

# --- network ------------------------------------------------------------------
ui_step "Checking network connectivity…"
if ! curl -s --connect-timeout 5 "https://api.github.com" >/dev/null 2>&1; then
  ui_error "Cannot reach GitHub. Check your network connection."
  exit 1
fi
ui_ok "Network OK"

if ! command -v jq >/dev/null 2>&1 || ! command -v curl >/dev/null 2>&1; then
  ui_error "curl and jq are required. Please install them."
  exit 1
fi

# --- backup current config ----------------------------------------------------
create_backup() {
  local backup_path="$BACKUP_DIR/backup_$(date +%Y%m%d_%H%M%S)"
  ui_step "Backing up configuration to $backup_path"
  mkdir -p "$backup_path"
  if [ -d "$SERVER_INSTALL_DIR" ]; then
    mkdir -p "$backup_path/server"
    cp "$SERVER_INSTALL_DIR/config.json"      "$backup_path/server/" 2>/dev/null || true
    cp "$SERVER_INSTALL_DIR/appsettings.json" "$backup_path/server/" 2>/dev/null || true
  fi
  if [ -d "$CLIENT_INSTALL_DIR" ]; then
    mkdir -p "$backup_path/client"
    cp "$CLIENT_INSTALL_DIR/ClientConfig.json" "$backup_path/client/" 2>/dev/null || true
  fi
  # keep only the most recent 3 backups
  local count
  count=$(find "$BACKUP_DIR" -maxdepth 1 -type d -name "backup_*" 2>/dev/null | wc -l)
  if [ "$count" -gt 3 ]; then
    find "$BACKUP_DIR" -maxdepth 1 -type d -name "backup_*" -print0 | sort -z | head -z -n $((count - 3)) | xargs -0 rm -rf
  fi
  ui_ok "Backup created"
}

# --- version discovery --------------------------------------------------------
ui_step "Checking for updates ($([ "$PRE_RELEASE" = true ] && echo pre-release || echo stable))…"
if [ "$PRE_RELEASE" = true ]; then
  # -L so a renamed/redirected repo (301) is followed transparently.
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$RR_GITHUB_REPO/releases" | jq -r '.[0]')
else
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$RR_GITHUB_REPO/releases/latest")
fi

LATEST_TAG=$(printf '%s' "$RELEASE_JSON" | jq -r '.tag_name // empty')
LATEST_VERSION=${LATEST_TAG#v}
if [ -z "$LATEST_TAG" ] || [ "$LATEST_TAG" = "null" ]; then
  ui_error "Failed to fetch latest release from GitHub ($RR_GITHUB_REPO)."
  exit 1
fi

LOCAL_VERSION="0.0.0"
if [ -x "$SERVER_INSTALL_DIR/RemoteRelay.Server" ]; then
  LOCAL_VERSION=$(rr_binary_version "$SERVER_INSTALL_DIR/RemoteRelay.Server")
elif [ -x "$CLIENT_INSTALL_DIR/RemoteRelay" ]; then
  LOCAL_VERSION=$(rr_binary_version "$CLIENT_INSTALL_DIR/RemoteRelay")
fi
[ -z "$LOCAL_VERSION" ] && LOCAL_VERSION="0.0.0"

version_to_int() { echo "$@" | awk -F. '{ printf("%d%03d%03d\n", $1,$2,$3); }'; }

ui_info "Current version: $LOCAL_VERSION"
ui_info "Latest version : $LATEST_VERSION"

if [ "$FORCE" != true ] && [ "$(version_to_int "$LATEST_VERSION")" -le "$(version_to_int "$LOCAL_VERSION")" ]; then
  ui_ok "RemoteRelay is already up to date."
  ui_msgbox "Up to date" "RemoteRelay $LOCAL_VERSION is the latest $([ "$PRE_RELEASE" = true ] && echo pre-release || echo release)."
  exit 0
fi

if [ "$FORCE" = true ]; then
  ui_step "Reinstalling $LATEST_VERSION (forced)…"
else
  ui_step "Update available: $LOCAL_VERSION → $LATEST_VERSION"
fi

create_backup

# --- pick the installer asset for this architecture ---------------------------
RUNTIME="$(rr_detect_runtime)"
case "$RUNTIME" in
  linux-arm64|linux-arm|linux-x64) ;;
  *) ui_error "Unsupported architecture '$(uname -m)' for the installer."; exit 1 ;;
esac

ASSET_URL=$(printf '%s' "$RELEASE_JSON" \
  | jq -r --arg rt "${RUNTIME}.sh" '.assets[]? | select(.name | endswith($rt)) | .browser_download_url' \
  | head -n1)

if [ -z "$ASSET_URL" ] || [ "$ASSET_URL" = "null" ]; then
  ui_error "Could not find an installer for $RUNTIME in release $LATEST_TAG."
  exit 1
fi

TMP_INSTALLER="$(mktemp /tmp/remoterelay_update.XXXXXX.sh)"
trap 'rm -f "$TMP_INSTALLER"' EXIT
ui_step "Downloading $(basename "$ASSET_URL")…"
curl -fSL --progress-bar -o "$TMP_INSTALLER" "$ASSET_URL" || { ui_error "Download failed."; exit 1; }

DL_SIZE=$(stat -c%s "$TMP_INSTALLER" 2>/dev/null || stat -f%z "$TMP_INSTALLER" 2>/dev/null || echo 0)
if [ "$DL_SIZE" -lt 100000 ]; then
  ui_error "Downloaded file is too small ($DL_SIZE bytes) — aborting."
  exit 1
fi
ui_ok "Downloaded ($((DL_SIZE / 1024 / 1024)) MB)"
chmod +x "$TMP_INSTALLER"

ui_step "Running installer…"
"$TMP_INSTALLER" "${INSTALLER_ARGS[@]}"
ui_ok "Update complete."
