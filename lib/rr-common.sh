# shellcheck shell=bash
# rr-common.sh — shared paths, user resolution, install metadata, and JSON
# helpers used by install.sh, update.sh, uninstall.sh and the `remoterelay`
# management tool. Sourced, not executed.

[ -n "${RR_COMMON_SOURCED:-}" ] && return 0
RR_COMMON_SOURCED=1

# Default GitHub repository that hosts releases. Overridable via install.conf or
# the RR_GITHUB_REPO environment variable.
RR_GITHUB_REPO="${RR_GITHUB_REPO:-keatesg/RemoteRelay}"

RR_SERVER_SERVICE_NAME="remote-relay-server.service"
RR_SERVER_SERVICE_FILE="/etc/systemd/system/${RR_SERVER_SERVICE_NAME}"
RR_INSTALL_CONF_DIR="/etc/remoterelay"
RR_INSTALL_CONF="${RR_INSTALL_CONF_DIR}/install.conf"
RR_MANAGE_BIN="/usr/local/bin/remoterelay"

# --- Architecture -> .NET runtime identifier ---------------------------------
# Echoes the runtime id matching the current machine, or empty if unsupported.
rr_detect_runtime() {
  case "$(uname -m)" in
    aarch64|arm64)        printf 'linux-arm64' ;;
    armv7l|armv6l|armhf|arm) printf 'linux-arm' ;;
    x86_64|amd64)         printf 'linux-x64' ;;
    *)                    printf '' ;;
  esac
}

# --- Target user / home resolution -------------------------------------------
# Resolves APP_USER and USER_HOME. Prefers the value recorded at install time;
# otherwise falls back to the user behind sudo. Returns non-zero on failure.
rr_resolve_user() {
  if [ -z "${APP_USER:-}" ]; then
    APP_USER="${SUDO_USER:-}"
  fi
  if [ -z "$APP_USER" ] || [ "$APP_USER" = "root" ]; then
    ui_error "Unable to determine the non-root user that owns the installation."
    ui_info  "Re-run via: sudo -E <command>"
    return 1
  fi
  USER_HOME="$(eval echo "~$APP_USER")"
  if [ -z "$USER_HOME" ] || [ ! -d "$USER_HOME" ]; then
    ui_error "Could not resolve the home directory for '$APP_USER'."
    return 1
  fi
  return 0
}

# Populates the canonical install paths from $USER_HOME. Call after the user is
# known (rr_resolve_user or rr_read_install_conf).
rr_set_paths() {
  BASE_INSTALL_DIR="${BASE_INSTALL_DIR:-$USER_HOME/RemoteRelay}"
  SERVER_INSTALL_DIR="${SERVER_INSTALL_DIR:-$BASE_INSTALL_DIR/server}"
  CLIENT_INSTALL_DIR="${CLIENT_INSTALL_DIR:-$BASE_INSTALL_DIR/client}"
  if [ -f "/etc/remoterelay/config.json" ]; then
    SERVER_CONFIG="/etc/remoterelay/config.json"
  else
    SERVER_CONFIG="$SERVER_INSTALL_DIR/config.json"
  fi
  if [ -f "$USER_HOME/.config/RemoteRelay/ClientConfig.json" ]; then
    CLIENT_CONFIG="$USER_HOME/.config/RemoteRelay/ClientConfig.json"
  else
    CLIENT_CONFIG="$CLIENT_INSTALL_DIR/ClientConfig.json"
  fi
  LIB_INSTALL_DIR="$BASE_INSTALL_DIR/lib"
}

# --- Install metadata ---------------------------------------------------------
# install.conf is a simple KEY=VALUE file recording where things live so the
# management tool and updater don't have to re-derive them.
rr_read_install_conf() {
  if [ -f "$RR_INSTALL_CONF" ]; then
    # shellcheck disable=SC1090
    . "$RR_INSTALL_CONF"
    [ -n "${RR_CONF_REPO:-}" ] && RR_GITHUB_REPO="$RR_CONF_REPO"
    [ -n "${RR_CONF_APP_USER:-}" ] && APP_USER="$RR_CONF_APP_USER"
    [ -n "${RR_CONF_BASE_DIR:-}" ] && BASE_INSTALL_DIR="$RR_CONF_BASE_DIR"
    return 0
  fi
  return 1
}

# rr_write_install_conf <channel>
rr_write_install_conf() {
  local channel="${1:-stable}"
  mkdir -p "$RR_INSTALL_CONF_DIR"
  cat > "$RR_INSTALL_CONF" <<EOF
# RemoteRelay install metadata — written by the installer. Edit with care.
RR_CONF_APP_USER="$APP_USER"
RR_CONF_BASE_DIR="$BASE_INSTALL_DIR"
RR_CONF_REPO="$RR_GITHUB_REPO"
RR_CONF_CHANNEL="$channel"
EOF
  chmod 644 "$RR_INSTALL_CONF"
}

# --- Rollback metadata ----------------------------------------------------------
# Written by update.sh just before it replaces an installation, in a separate
# file so the installer's rewrite of install.conf can't clobber it.
RR_ROLLBACK_CONF="${RR_INSTALL_CONF_DIR}/rollback.conf"

# rr_write_rollback_conf <version> <backup-dir>
rr_write_rollback_conf() {
  mkdir -p "$RR_INSTALL_CONF_DIR"
  cat > "$RR_ROLLBACK_CONF" <<EOF
# RemoteRelay rollback metadata — written by update.sh before an update.
RR_ROLLBACK_VERSION="$1"
RR_ROLLBACK_BACKUP="$2"
EOF
  chmod 644 "$RR_ROLLBACK_CONF"
}

# Sources rollback metadata into RR_ROLLBACK_VERSION / RR_ROLLBACK_BACKUP.
# Returns non-zero when there is nothing to roll back to.
rr_read_rollback_conf() {
  [ -f "$RR_ROLLBACK_CONF" ] || return 1
  # shellcheck disable=SC1090
  . "$RR_ROLLBACK_CONF"
  [ -n "${RR_ROLLBACK_VERSION:-}" ]
}

# --- Component / service state ------------------------------------------------
rr_server_binary() {
  if [ -x "/usr/bin/remoterelay-server" ]; then
    printf '/usr/bin/remoterelay-server'
  elif [ -x "$SERVER_INSTALL_DIR/RemoteRelay.Server" ]; then
    printf '%s' "$SERVER_INSTALL_DIR/RemoteRelay.Server"
  fi
}

rr_client_binary() {
  if [ -x "/usr/bin/remoterelay-client" ]; then
    printf '/usr/bin/remoterelay-client'
  elif [ -x "$CLIENT_INSTALL_DIR/RemoteRelay" ]; then
    printf '%s' "$CLIENT_INSTALL_DIR/RemoteRelay"
  fi
}

rr_server_installed() { [ -n "$(rr_server_binary)" ]; }
rr_client_installed() { [ -n "$(rr_client_binary)" ]; }

rr_service_active()  { systemctl is-active  --quiet "$RR_SERVER_SERVICE_NAME"; }
rr_service_enabled() { systemctl is-enabled --quiet "$RR_SERVER_SERVICE_NAME" 2>/dev/null; }

# Reads a version from an installed binary; prints "n/a" if unavailable.
rr_binary_version() {
  local bin="$1"
  if [ -x "$bin" ]; then
    "$bin" --version 2>/dev/null | head -n1 || printf 'n/a'
  else
    printf 'n/a'
  fi
}

# --- JSON helpers (require jq) ------------------------------------------------
rr_have_jq() { command -v jq >/dev/null 2>&1; }

# rr_json_get <file> <jq-filter>  -> prints result (empty on miss/error)
rr_json_get() {
  local file="$1" filter="$2"
  [ -f "$file" ] || return 0
  rr_have_jq || return 0
  jq -r "$filter // empty" "$file" 2>/dev/null || true
}

# rr_json_set <file> <jq-program> [extra jq args...]
# Applies the jq program to the file in place, keeping a timestamped backup and
# preserving ownership. Extra args (e.g. --arg name value) are passed to jq.
rr_json_set() {
  local file="$1"; shift
  local program="$1"; shift
  rr_have_jq || { ui_error "jq is required to edit $file"; return 1; }
  if [ ! -f "$file" ]; then printf '{}' > "$file"; fi
  local backup="${file}.bak-$(date +%Y%m%d_%H%M%S)"
  cp "$file" "$backup" 2>/dev/null || true
  local tmp; tmp="$(mktemp)"
  if jq "$@" "$program" "$file" > "$tmp" 2>/dev/null && [ -s "$tmp" ]; then
    mv "$tmp" "$file"
    if [ -n "${APP_USER:-}" ]; then chown "$APP_USER:$APP_USER" "$file" 2>/dev/null || true; fi
    return 0
  fi
  rm -f "$tmp"
  ui_error "Failed to update $file"
  return 1
}
