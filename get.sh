#!/bin/bash
#
# RemoteRelay bootstrap installer.
#
#   curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash
#
# Optionally pass flags through the pipe:
#
#   ... | sudo bash -s -- --pre-release      # install the latest pre-release
#   ... | sudo bash -s -- --server-only      # install only the server component
#   ... | sudo bash -s -- --client-only      # install only the client component
#   ... | sudo bash -s -- --unattended       # never prompt; use safe defaults
#
# It detects your CPU architecture, downloads the matching self-extracting
# installer from the latest GitHub release, and runs it. The installer then
# guides you through (or, when unattended, silently performs) the setup.
#
set -euo pipefail

GITHUB_REPO="${RR_GITHUB_REPO:-keatesg/RemoteRelay}"
PRE_RELEASE=false
PASS_ARGS=()

# --- minimal self-contained colours (this script can't source rr-ui.sh) ------
if [ -t 1 ] && [ "${TERM:-dumb}" != "dumb" ]; then
  C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_RED=$'\033[31m'
  C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'
else
  C_RESET=''; C_BOLD=''; C_RED=''; C_GREEN=''; C_YELLOW=''; C_CYAN=''
fi
say()  { printf '%s▸%s %s\n' "$C_CYAN" "$C_RESET" "$1"; }
ok()   { printf '%s  ✓%s %s\n' "$C_GREEN" "$C_RESET" "$1"; }
warn() { printf '%s  ⚠ %s%s\n' "$C_YELLOW" "$1" "$C_RESET" >&2; }
die()  { printf '%s  ✗ %s%s\n' "$C_RED" "$1" "$C_RESET" >&2; exit 1; }

printf '%s%s\n' "$C_BOLD" "RemoteRelay installer${C_RESET}"
printf '%s\n\n' "Fetching the right build for this machine…"

# --- parse args ---------------------------------------------------------------
while [ "$#" -gt 0 ]; do
  case "$1" in
    -p|--pre-release) PRE_RELEASE=true; PASS_ARGS+=("--pre-release") ;;
    --unattended)     export RR_UNATTENDED=1; PASS_ARGS+=("--unattended") ;;
    --server-only)    PASS_ARGS+=("--server-only") ;;
    --client-only)    PASS_ARGS+=("--client-only") ;;
    *) warn "Ignoring unknown option: $1" ;;
  esac
  shift
done

# --- root check ---------------------------------------------------------------
if [ "$(id -u)" -ne 0 ]; then
  die "This installer must run as root. Re-run with:
    curl -fsSL https://raw.githubusercontent.com/${GITHUB_REPO}/master/get.sh | sudo bash"
fi

# --- no controlling terminal => fall back to unattended -----------------------
# When piped straight into bash with no tty (e.g. an automated provisioner),
# we can't prompt, so install non-interactively with sensible defaults.
if [ "${RR_UNATTENDED:-0}" != "1" ] && ! { : </dev/tty; } 2>/dev/null; then
  warn "No interactive terminal detected — installing with default options."
  export RR_UNATTENDED=1
fi

# --- prerequisites ------------------------------------------------------------
ensure_deps() {
  local missing=()
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  command -v jq   >/dev/null 2>&1 || missing+=("jq")
  # whiptail powers the nicer UI; not fatal if it can't be installed.
  command -v whiptail >/dev/null 2>&1 || missing+=("whiptail")
  [ "${#missing[@]}" -eq 0 ] && return 0

  if command -v apt-get >/dev/null 2>&1; then
    say "Installing prerequisites: ${missing[*]}"
    apt-get update -qq || true
    apt-get install -y -qq "${missing[@]}" || warn "Some prerequisites failed to install."
  else
    warn "Please install these packages manually: ${missing[*]}"
  fi
  # curl and jq are mandatory; whiptail is optional (text fallback exists).
  command -v curl >/dev/null 2>&1 || die "curl is required."
  command -v jq   >/dev/null 2>&1 || die "jq is required."
}
ensure_deps

# --- architecture -------------------------------------------------------------
ARCH="$(uname -m)"
case "$ARCH" in
  aarch64|arm64)           RUNTIME="linux-arm64" ;;
  armv7l|armv6l|armhf|arm) RUNTIME="linux-arm" ;;
  x86_64|amd64)            RUNTIME="linux-x64" ;;
  *) die "Unsupported architecture '$ARCH'. Installers are published for linux-arm64, linux-arm and linux-x64." ;;
esac
ok "Architecture: $ARCH ($RUNTIME)"

# --- locate the installer asset on the latest release -------------------------
API="https://api.github.com/repos/${GITHUB_REPO}"
if [ "$PRE_RELEASE" = true ]; then
  say "Looking up the latest pre-release…"
  RELEASE_JSON="$(curl -fsSL "${API}/releases" | jq -r '.[0]')"
else
  say "Looking up the latest release…"
  RELEASE_JSON="$(curl -fsSL "${API}/releases/latest")"
fi

[ -n "$RELEASE_JSON" ] && [ "$RELEASE_JSON" != "null" ] || \
  die "Could not fetch release information from GitHub for ${GITHUB_REPO}."

TAG="$(printf '%s' "$RELEASE_JSON" | jq -r '.tag_name // empty')"
# Match the makeself asset for this runtime exactly (…linux-arm.sh vs …linux-arm64.sh).
ASSET_URL="$(printf '%s' "$RELEASE_JSON" \
  | jq -r --arg rt "${RUNTIME}.sh" '.assets[]? | select(.name | endswith($rt)) | .browser_download_url' \
  | head -n1)"

[ -n "$ASSET_URL" ] && [ "$ASSET_URL" != "null" ] || \
  die "No installer for $RUNTIME found in release ${TAG:-?}."

ok "Found ${TAG:-release}: $(basename "$ASSET_URL")"

# --- dry run? -----------------------------------------------------------------
if [ "${RR_DRY_RUN:-0}" = "1" ]; then
  printf '\n%sDry run%s — would download and execute:\n  %s\n' "$C_BOLD" "$C_RESET" "$ASSET_URL"
  printf 'Passing arguments: %s\n' "${PASS_ARGS[*]:-(none)}"
  printf 'Unattended: %s\n' "${RR_UNATTENDED:-0}"
  exit 0
fi

# --- download + run -----------------------------------------------------------
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
INSTALLER="$TMP_DIR/remote-relay-installer.sh"

say "Downloading installer…"
curl -fSL --progress-bar -o "$INSTALLER" "$ASSET_URL" || die "Download failed."

DL_SIZE="$(stat -c%s "$INSTALLER" 2>/dev/null || stat -f%z "$INSTALLER" 2>/dev/null || echo 0)"
[ "$DL_SIZE" -ge 100000 ] || die "Downloaded installer looks too small (${DL_SIZE} bytes) — aborting."
chmod +x "$INSTALLER"
ok "Downloaded ($((DL_SIZE / 1024 / 1024)) MB)"

say "Launching installer…"
# Export so the makeself-extracted install.sh inherits the headless flag.
export RR_UNATTENDED="${RR_UNATTENDED:-0}"
"$INSTALLER" "${PASS_ARGS[@]}"
