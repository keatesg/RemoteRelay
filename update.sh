#!/bin/bash
#
# RemoteRelay updater — fetches a release from GitHub and installs it over the
# current installation. Usually invoked via `sudo remoterelay update`.
#
#   sudo ./update.sh                   update to the latest stable release
#   sudo ./update.sh --pre-release     update to the latest pre-release
#   sudo ./update.sh --force           reinstall even if already up to date
#   sudo ./update.sh --version v1.2.3  install a specific release (up or down)
#   sudo ./update.sh --rollback        return to the version replaced by the
#                                      last update, restoring its config backup
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
ROLLBACK=false
TARGET_VERSION=""
INSTALLER_ARGS=()
# Default channel comes from how the system was installed.
RR_CONF_CHANNEL="stable"
while [ "$#" -gt 0 ]; do
  case "$1" in
    -p|--pre-release) PRE_RELEASE=true ;;
    -f|--force)       FORCE=true ;;
    --rollback)       ROLLBACK=true ;;
    --version)
      shift
      TARGET_VERSION="${1:-}"
      [ -n "$TARGET_VERSION" ] || { ui_error "--version requires a release tag (e.g. v1.2.3)."; exit 1; }
      ;;
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

# --- resolve rollback request ---------------------------------------------------
# Rolling back means: install the version the last update replaced, then restore
# the config backup taken just before that update.
ROLLBACK_RESTORE_DIR=""
if [ "$ROLLBACK" = true ]; then
  if [ -n "$TARGET_VERSION" ]; then
    ui_error "--rollback and --version cannot be combined."
    exit 1
  fi
  if ! rr_read_rollback_conf; then
    ui_error "Nothing to roll back to — no previous update has been recorded."
    exit 1
  fi
  TARGET_VERSION="$RR_ROLLBACK_VERSION"
  ROLLBACK_RESTORE_DIR="${RR_ROLLBACK_BACKUP:-}"
  ui_info "Rolling back to version $TARGET_VERSION"
fi

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
# Sets BACKUP_PATH to the directory created, for the rollback metadata.
BACKUP_PATH=""
create_backup() {
  local backup_path="$BACKUP_DIR/backup_$(date +%Y%m%d_%H%M%S)"
  ui_step "Backing up configuration to $backup_path"
  mkdir -p "$backup_path"
  BACKUP_PATH="$backup_path"
  # Keep this list in sync with the files install.sh preserves across reinstalls.
  if [ -d "$SERVER_INSTALL_DIR" ]; then
    mkdir -p "$backup_path/server"
    cp "$SERVER_INSTALL_DIR/config.json"                  "$backup_path/server/" 2>/dev/null || true
    cp "$SERVER_INSTALL_DIR/appsettings.json"             "$backup_path/server/" 2>/dev/null || true
    cp "$SERVER_INSTALL_DIR/appsettings.Development.json" "$backup_path/server/" 2>/dev/null || true
  fi
  if [ -d "$CLIENT_INSTALL_DIR" ]; then
    mkdir -p "$backup_path/client"
    cp "$CLIENT_INSTALL_DIR/ClientConfig.json"  "$backup_path/client/" 2>/dev/null || true
    cp "$CLIENT_INSTALL_DIR/ServerDetails.json" "$backup_path/client/" 2>/dev/null || true
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
# Fetches a release by its git tag; tolerates a missing/extra "v" prefix.
fetch_release_by_tag() {
  local requested="$1" tag json
  for tag in "$requested" "v${requested#v}" "${requested#v}"; do
    json=$(curl -fsSL "https://api.github.com/repos/$RR_GITHUB_REPO/releases/tags/$tag" 2>/dev/null)
    if [ -n "$json" ] && [ "$(printf '%s' "$json" | jq -r '.tag_name // empty')" != "" ]; then
      printf '%s' "$json"
      return 0
    fi
  done
  return 1
}

if [ -n "$TARGET_VERSION" ]; then
  ui_step "Looking up release $TARGET_VERSION…"
  if ! RELEASE_JSON=$(fetch_release_by_tag "$TARGET_VERSION"); then
    ui_error "Release '$TARGET_VERSION' not found on GitHub ($RR_GITHUB_REPO)."
    exit 1
  fi
elif [ "$PRE_RELEASE" = true ]; then
  ui_step "Checking for updates (pre-release)…"
  # -L so a renamed/redirected repo (301) is followed transparently.
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$RR_GITHUB_REPO/releases" | jq -r '.[0]')
else
  ui_step "Checking for updates (stable)…"
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
ui_info "Target version : $LATEST_VERSION"

if [ -n "$TARGET_VERSION" ]; then
  # Explicitly targeted release: downgrades are allowed, only skip a no-op.
  if [ "$FORCE" != true ] && [ "$LOCAL_VERSION" = "$LATEST_VERSION" ]; then
    ui_ok "RemoteRelay $LOCAL_VERSION is already installed."
    exit 0
  fi
  ui_step "Installing $LATEST_VERSION (currently $LOCAL_VERSION)…"
elif [ "$FORCE" != true ] && [ "$(version_to_int "$LATEST_VERSION")" -le "$(version_to_int "$LOCAL_VERSION")" ]; then
  ui_ok "RemoteRelay is already up to date."
  ui_msgbox "Up to date" "RemoteRelay $LOCAL_VERSION is the latest $([ "$PRE_RELEASE" = true ] && echo pre-release || echo release)."
  exit 0
elif [ "$FORCE" = true ]; then
  ui_step "Reinstalling $LATEST_VERSION (forced)…"
else
  ui_step "Update available: $LOCAL_VERSION → $LATEST_VERSION"
fi

create_backup

# Record what this update replaces so `remoterelay rollback` can undo it. A
# rollback records the same thing, so rolling back is itself reversible. Skip
# when nothing actually changes, or when there's no usable current version.
if [ "$LOCAL_VERSION" != "$LATEST_VERSION" ] && [[ "$LOCAL_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  rr_write_rollback_conf "$LOCAL_VERSION" "$BACKUP_PATH"
fi

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

# --- verify integrity against the release's SHA256SUMS manifest ----------------
# Older releases don't ship one; warn and continue in that case.
SUMS_URL=$(printf '%s' "$RELEASE_JSON" \
  | jq -r '.assets[]? | select(.name == "SHA256SUMS") | .browser_download_url' \
  | head -n1)
if [ -n "$SUMS_URL" ] && [ "$SUMS_URL" != "null" ]; then
  ASSET_NAME="$(basename "$ASSET_URL")"
  EXPECTED=$(curl -fsSL "$SUMS_URL" | awk -v f="$ASSET_NAME" '$2 == f { print $1 }')
  if [ -z "$EXPECTED" ]; then
    ui_warn "No entry for $ASSET_NAME in SHA256SUMS — skipping checksum verification."
  else
    ACTUAL=$(sha256sum "$TMP_INSTALLER" | awk '{ print $1 }')
    if [ "$ACTUAL" != "$EXPECTED" ]; then
      ui_error "Checksum mismatch for $ASSET_NAME — the download may be corrupt or tampered with."
      ui_info  "expected: $EXPECTED"
      ui_info  "actual  : $ACTUAL"
      exit 1
    fi
    ui_ok "Checksum verified"
  fi
else
  ui_warn "Release $LATEST_TAG has no SHA256SUMS manifest — skipping checksum verification."
fi

ui_step "Running installer…"
"$TMP_INSTALLER" "${INSTALLER_ARGS[@]}"

# --- rollback: restore the config that matched the reinstated version ----------
if [ "$ROLLBACK" = true ]; then
  if [ -n "$ROLLBACK_RESTORE_DIR" ] && [ -d "$ROLLBACK_RESTORE_DIR" ]; then
    ui_step "Restoring configuration from $(basename "$ROLLBACK_RESTORE_DIR")…"
    for f in config.json appsettings.json appsettings.Development.json; do
      if [ -f "$ROLLBACK_RESTORE_DIR/server/$f" ]; then
        cp "$ROLLBACK_RESTORE_DIR/server/$f" "$SERVER_INSTALL_DIR/$f" && \
          chown "$APP_USER:$APP_USER" "$SERVER_INSTALL_DIR/$f" 2>/dev/null || true
      fi
    done
    for f in ClientConfig.json ServerDetails.json; do
      if [ -f "$ROLLBACK_RESTORE_DIR/client/$f" ]; then
        cp "$ROLLBACK_RESTORE_DIR/client/$f" "$CLIENT_INSTALL_DIR/$f" && \
          chown "$APP_USER:$APP_USER" "$CLIENT_INSTALL_DIR/$f" 2>/dev/null || true
      fi
    done
    ui_ok "Configuration restored."
    if systemctl is-active --quiet "$RR_SERVER_SERVICE_NAME" 2>/dev/null; then
      ui_step "Restarting server with restored configuration…"
      systemctl restart "$RR_SERVER_SERVICE_NAME" 2>/dev/null || ui_warn "Server failed to restart — check logs."
    fi
  else
    ui_warn "Config backup from before the update no longer exists — keeping current config."
  fi
  ui_ok "Rollback to $LATEST_VERSION complete."
else
  ui_ok "Update complete."
fi
