# shellcheck shell=bash
# rr-ui.sh — shared terminal UI helpers for the RemoteRelay shell tooling.
#
# Provides a small dialog API that renders with `whiptail` when available and a
# controlling terminal exists, and degrades gracefully to ANSI-coloured text
# prompts otherwise. In unattended mode every prompt resolves to its supplied
# default with no I/O, so the same scripts drive both interactive installs and
# `curl ... | sudo bash` headless runs.
#
# This file is meant to be *sourced*, not executed.
#
# Public functions:
#   ui_header <text>                       banner line
#   ui_step <text> / ui_info / ui_ok       progress narration
#   ui_warn <text> / ui_error <text>       warnings/errors (stderr)
#   ui_msgbox <title> <message>            informational box
#   ui_yesno <title> <message> [default]   yes/no -> return 0 (yes) / 1 (no)
#   ui_inputbox <title> <prompt> [default] -> echoes entered value
#   ui_menu <title> <prompt> tag item ...  -> echoes chosen tag
#   ui_radiolist <title> <prompt> <default> tag item ...   -> echoes chosen tag
#   ui_checklist <title> <prompt> tag item on|off ...      -> echoes chosen tags
#   ui_gauge <title> <message>             read 0-100 ints from stdin -> progress
#   ui_textbox <title> <file>              show a file
#   ui_tailbox <title> <file>              follow a growing file (whiptail only)
#
# Environment:
#   RR_UNATTENDED=1   never prompt; prompts return their default
#   RR_NO_WHIPTAIL=1  force the text fallback even if whiptail is installed
#   RR_BACKTITLE      backtitle shown on whiptail screens

[ -n "${RR_UI_SOURCED:-}" ] && return 0
RR_UI_SOURCED=1

RR_BACKTITLE="${RR_BACKTITLE:-RemoteRelay}"

# --- Capability detection -----------------------------------------------------

# RR_INTERACTIVE: 1 when we may prompt the user (a usable controlling tty and
# not running unattended). `: </dev/tty` fails with ENXIO when there is no
# controlling terminal (e.g. a detached headless pipe), which is exactly when we
# must fall back to defaults rather than block forever on a prompt.
RR_INTERACTIVE=0
if [ "${RR_UNATTENDED:-0}" != "1" ] && { : </dev/tty; } 2>/dev/null; then
  RR_INTERACTIVE=1
fi

RR_HAS_WHIPTAIL=0
if [ "${RR_NO_WHIPTAIL:-0}" != "1" ] && command -v whiptail >/dev/null 2>&1; then
  RR_HAS_WHIPTAIL=1
fi

# Use whiptail only when it is present *and* we have a terminal to draw on.
_ui_use_whiptail() { [ "$RR_HAS_WHIPTAIL" = 1 ] && [ "$RR_INTERACTIVE" = 1 ]; }

# --- Colours ------------------------------------------------------------------

if [ -t 1 ] && [ "${TERM:-dumb}" != "dumb" ]; then
  RR_C_RESET=$'\033[0m'; RR_C_BOLD=$'\033[1m'; RR_C_DIM=$'\033[2m'
  RR_C_RED=$'\033[31m'; RR_C_GREEN=$'\033[32m'; RR_C_YELLOW=$'\033[33m'
  RR_C_BLUE=$'\033[34m'; RR_C_CYAN=$'\033[36m'
else
  RR_C_RESET=''; RR_C_BOLD=''; RR_C_DIM=''
  RR_C_RED=''; RR_C_GREEN=''; RR_C_YELLOW=''; RR_C_BLUE=''; RR_C_CYAN=''
fi

# --- Narration (always plain text to stdout/stderr) ---------------------------

ui_header() {
  printf '\n%s%s══ %s ══%s\n' "$RR_C_BOLD" "$RR_C_CYAN" "$1" "$RR_C_RESET"
}
ui_step()  { printf '%s▸%s %s\n'  "$RR_C_BLUE"   "$RR_C_RESET" "$1"; }
ui_info()  { printf '  %s\n' "$1"; }
ui_ok()    { printf '%s  ✓%s %s\n' "$RR_C_GREEN"  "$RR_C_RESET" "$1"; }
ui_warn()  { printf '%s  ⚠ %s%s\n' "$RR_C_YELLOW" "$1" "$RR_C_RESET" >&2; }
ui_error() { printf '%s  ✗ %s%s\n' "$RR_C_RED"    "$1" "$RR_C_RESET" >&2; }

# --- whiptail invocation wrapper ----------------------------------------------
# whiptail draws to the terminal and writes its result to fd 2; the 3>&1 1>&2 2>&3
# dance routes that result to stdout for capture. stdin is taken from /dev/tty so
# the keyboard works even when the script itself arrived over a pipe.
_wt() {
  whiptail --backtitle "$RR_BACKTITLE" "$@" 3>&1 1>&2 2>&3 </dev/tty
}

# Read a single line from the real terminal regardless of script stdin.
_ui_read() {
  # shellcheck disable=SC2229  # intentional read into named var via -p style
  local __var="$1" __prompt="$2" __default="$3" __reply=""
  if [ -n "$__default" ]; then
    printf '%s%s%s [%s]: %s' "$RR_C_BOLD" "$__prompt" "$RR_C_RESET" "$__default" "" >/dev/tty
  else
    printf '%s%s%s: ' "$RR_C_BOLD" "$__prompt" "$RR_C_RESET" >/dev/tty
  fi
  IFS= read -r __reply </dev/tty || __reply=""
  [ -z "$__reply" ] && __reply="$__default"
  printf -v "$__var" '%s' "$__reply"
}

# --- Dialog API ---------------------------------------------------------------

ui_msgbox() {
  local title="$1" msg="$2"
  if _ui_use_whiptail; then
    _wt --title "$title" --msgbox "$msg" 20 76 --scrolltext
  else
    printf '\n%s%s%s\n%s\n' "$RR_C_BOLD" "$title" "$RR_C_RESET" "$msg"
    [ "$RR_INTERACTIVE" = 1 ] && { printf '%sPress Enter to continue...%s' "$RR_C_DIM" "$RR_C_RESET" >/dev/tty; read -r _ </dev/tty || true; }
  fi
}

# ui_yesno <title> <message> [default yes|no]  -> 0 yes, 1 no
ui_yesno() {
  local title="$1" msg="$2" default="${3:-yes}"
  if [ "$RR_INTERACTIVE" != 1 ]; then
    [ "$default" = yes ]; return
  fi
  if _ui_use_whiptail; then
    local defargs=()
    [ "$default" = no ] && defargs=(--defaultno)
    _wt --title "$title" "${defargs[@]}" --yesno "$msg" 14 76
    return
  fi
  local hint="[Y/n]"; [ "$default" = no ] && hint="[y/N]"
  local reply
  while true; do
    printf '%s%s%s %s %s ' "$RR_C_BOLD" "$msg" "$RR_C_RESET" "$hint" "" >/dev/tty
    IFS= read -r reply </dev/tty || reply=""
    reply="${reply:-$default}"
    case "${reply,,}" in
      y|yes) return 0 ;;
      n|no)  return 1 ;;
      *) printf 'Please answer y or n.\n' >/dev/tty ;;
    esac
  done
}

# ui_inputbox <title> <prompt> [default] -> echoes value
ui_inputbox() {
  local title="$1" prompt="$2" default="${3:-}"
  if [ "$RR_INTERACTIVE" != 1 ]; then
    printf '%s' "$default"; return 0
  fi
  if _ui_use_whiptail; then
    _wt --title "$title" --inputbox "$prompt" 12 76 "$default"
    return
  fi
  local val; _ui_read val "$prompt" "$default"
  printf '%s' "$val"
}

# ui_menu <title> <prompt> tag1 item1 tag2 item2 ... -> echoes chosen tag
ui_menu() {
  local title="$1" prompt="$2"; shift 2
  if [ "$RR_INTERACTIVE" != 1 ]; then
    printf '%s' "${1:-}"; return 0   # first tag is the default
  fi
  if _ui_use_whiptail; then
    local n=$(( $# / 2 ))
    _wt --title "$title" --menu "$prompt" 20 76 "$n" "$@"
    return
  fi
  # Text fallback: numbered list
  local -a tags=() items=()
  while [ "$#" -ge 2 ]; do tags+=("$1"); items+=("$2"); shift 2; done
  printf '\n%s%s%s\n' "$RR_C_BOLD" "$prompt" "$RR_C_RESET" >/dev/tty
  local i
  for i in "${!tags[@]}"; do
    printf '  %s%2d)%s %s — %s\n' "$RR_C_CYAN" "$((i+1))" "$RR_C_RESET" "${tags[$i]}" "${items[$i]}" >/dev/tty
  done
  local choice
  while true; do
    printf '%sChoose [1-%d]: %s' "$RR_C_BOLD" "${#tags[@]}" "$RR_C_RESET" >/dev/tty
    IFS= read -r choice </dev/tty || choice=""
    if [[ "$choice" =~ ^[0-9]+$ ]] && [ "$choice" -ge 1 ] && [ "$choice" -le "${#tags[@]}" ]; then
      printf '%s' "${tags[$((choice-1))]}"; return 0
    fi
    printf 'Invalid selection.\n' >/dev/tty
  done
}

# ui_radiolist <title> <prompt> <default-tag> tag1 item1 tag2 item2 ... -> chosen tag
ui_radiolist() {
  local title="$1" prompt="$2" default="$3"; shift 3
  if [ "$RR_INTERACTIVE" != 1 ]; then
    printf '%s' "$default"; return 0
  fi
  if _ui_use_whiptail; then
    local -a args=(); local n=0
    while [ "$#" -ge 2 ]; do
      local on="off"; [ "$1" = "$default" ] && on="on"
      args+=("$1" "$2" "$on"); shift 2; n=$((n+1))
    done
    _wt --title "$title" --radiolist "$prompt" 20 76 "$n" "${args[@]}"
    return
  fi
  ui_menu "$title" "$prompt" "$@"
}

# ui_checklist <title> <prompt> tag1 item1 on|off  tag2 item2 on|off ...
#   -> echoes space-separated chosen tags (whiptail emits them quoted; we strip)
ui_checklist() {
  local title="$1" prompt="$2"; shift 2
  if _ui_use_whiptail; then
    local n=$(( $# / 3 ))
    local out
    out=$(_wt --title "$title" --checklist "$prompt" 20 76 "$n" "$@") || return 1
    # whiptail returns tags wrapped in double quotes: "a" "b"
    printf '%s' "${out//\"/}"
    return 0
  fi
  # Text/unattended fallback: select pre-checked (on) entries by default and let
  # an interactive user toggle each.
  local -a tags=() items=() states=()
  while [ "$#" -ge 3 ]; do tags+=("$1"); items+=("$2"); states+=("$3"); shift 3; done
  local i out=()
  for i in "${!tags[@]}"; do
    local chosen="${states[$i]}"
    if [ "$RR_INTERACTIVE" = 1 ]; then
      if ui_yesno "$title" "${items[$i]} (${tags[$i]})?" "$( [ "${states[$i]}" = on ] && echo yes || echo no )"; then
        chosen=on; else chosen=off; fi
    fi
    [ "$chosen" = on ] && out+=("${tags[$i]}")
  done
  printf '%s' "${out[*]}"
}

# ui_gauge <title> <message> : reads integer percentages (0-100) on stdin.
ui_gauge() {
  local title="$1" msg="$2"
  if _ui_use_whiptail; then
    whiptail --backtitle "$RR_BACKTITLE" --title "$title" --gauge "$msg" 8 76 0
  else
    local pct
    while IFS= read -r pct; do
      [[ "$pct" =~ ^[0-9]+$ ]] && printf '\r  %s … %s%%   ' "$msg" "$pct"
    done
    printf '\n'
  fi
}

ui_textbox() {
  local title="$1" file="$2"
  if [ ! -f "$file" ]; then ui_msgbox "$title" "File not found: $file"; return 0; fi
  if _ui_use_whiptail; then
    _wt --title "$title" --textbox "$file" 24 90 --scrolltext
  else
    printf '\n%s%s%s\n' "$RR_C_BOLD" "$title" "$RR_C_RESET"
    cat "$file"
    [ "$RR_INTERACTIVE" = 1 ] && { printf '%sPress Enter to continue...%s' "$RR_C_DIM" "$RR_C_RESET" >/dev/tty; read -r _ </dev/tty || true; }
  fi
}

ui_tailbox() {
  local title="$1" file="$2"
  if _ui_use_whiptail && [ -f "$file" ]; then
    _wt --title "$title" --tailbox "$file" 24 90 || true
  else
    ui_textbox "$title" "$file"
  fi
}
