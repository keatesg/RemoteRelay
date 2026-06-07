#!/bin/bash
#
# remoterelay — manage an installed RemoteRelay system.
# (Installed to /usr/local/bin/remoterelay; this source file is named
#  remoterelay.sh so it doesn't clash with the RemoteRelay/ directory on
#  case-insensitive filesystems.)
#
# Usage:
#   sudo remoterelay                 open the interactive menu
#   sudo remoterelay status          print status
#   sudo remoterelay start|stop|restart|enable|disable
#   sudo remoterelay update [--pre-release|--force]
#   sudo remoterelay config          open the configuration menu
#   sudo remoterelay logs            view server logs
#   sudo remoterelay uninstall
#   sudo remoterelay version
#
set -uo pipefail

VERSION="2.0.0"

# --- locate and load the shared libraries ------------------------------------
_self="$(readlink -f "$0" 2>/dev/null || echo "$0")"
_self_dir="$(dirname "$_self")"
RR_LIB=""
for d in "${RR_LIB_DIR:-}" /usr/local/lib/remoterelay "$_self_dir/lib" "$_self_dir"; do
  if [ -n "$d" ] && [ -f "$d/rr-ui.sh" ] && [ -f "$d/rr-common.sh" ]; then
    RR_LIB="$d"; break
  fi
done
if [ -z "$RR_LIB" ]; then
  echo "remoterelay: cannot find rr-ui.sh / rr-common.sh (looked in /usr/local/lib/remoterelay)." >&2
  exit 1
fi
# shellcheck source=lib/rr-ui.sh
. "$RR_LIB/rr-ui.sh"
# shellcheck source=lib/rr-common.sh
. "$RR_LIB/rr-common.sh"

RR_BACKTITLE="RemoteRelay management"

# --- bootstrap: root + paths --------------------------------------------------
require_root() {
  if [ "$(id -u)" -ne 0 ]; then
    ui_error "remoterelay must be run as root. Try: sudo remoterelay ${*:-}"
    exit 1
  fi
}

load_install() {
  rr_read_install_conf || true
  if ! rr_resolve_user; then
    ui_error "No RemoteRelay installation metadata found and no sudo user to fall back to."
    exit 1
  fi
  rr_set_paths
}

restart_if_running() {
  if rr_service_active; then
    ui_step "Restarting server to apply changes…"
    if systemctl restart "$RR_SERVER_SERVICE_NAME"; then ui_ok "Server restarted."; else ui_warn "Server failed to restart — check logs."; fi
  fi
}

# --- status -------------------------------------------------------------------
client_autostart_state() {
  local wf="$USER_HOME/.config/wayfire.ini"
  local xdg="$USER_HOME/.config/autostart/remote-relay-client.desktop"
  if { [ -f "$wf" ] && grep -q "remote-relay-client" "$wf" 2>/dev/null; } || [ -f "$xdg" ]; then
    echo "enabled"
  else
    echo "not configured"
  fi
}

build_status() {
  local active enabled svc_state="not installed"
  if [ -f "$RR_SERVER_SERVICE_FILE" ]; then
    active="stopped"; rr_service_active && active="running"
    enabled="disabled"; rr_service_enabled && enabled="enabled"
    svc_state="$active ($enabled at boot)"
  fi

  local sver cver port driver k8090 ips
  sver="$(rr_binary_version "$SERVER_INSTALL_DIR/RemoteRelay.Server")"
  cver="$(rr_binary_version "$CLIENT_INSTALL_DIR/RemoteRelay")"
  port="$(rr_json_get "$SERVER_CONFIG" '.ServerPort')"; port="${port:-not set}"
  driver="$(rr_json_get "$SERVER_CONFIG" '.RelayDriver')"; driver="${driver:-Auto}"
  k8090="$(rr_json_get "$SERVER_CONFIG" '.K8090.Port')"
  ips="$(hostname -I 2>/dev/null | tr -s ' ' | sed 's/ $//')"; ips="${ips:-unknown}"

  cat <<EOF
Install user : $APP_USER
Install path : $BASE_INSTALL_DIR

Server
  installed  : $(rr_server_installed && echo "yes ($sver)" || echo "no")
  service    : $svc_state
  listen port: $port
  relay driver: $driver$( [ -n "$k8090" ] && echo " ($k8090)" )
  addresses  : $ips

Client
  installed  : $(rr_client_installed && echo "yes ($cver)" || echo "no")
  autostart  : $(client_autostart_state)
EOF
}

do_status() {
  if [ "$RR_INTERACTIVE" = 1 ] && [ "$RR_HAS_WHIPTAIL" = 1 ]; then
    ui_msgbox "RemoteRelay status" "$(build_status)"
  else
    ui_header "RemoteRelay status"
    build_status
  fi
}

# --- service control ----------------------------------------------------------
svc() {
  local action="$1"
  ui_step "systemctl $action $RR_SERVER_SERVICE_NAME"
  if systemctl "$action" "$RR_SERVER_SERVICE_NAME"; then
    ui_ok "Done."
  else
    ui_error "systemctl $action failed."
  fi
}

service_menu() {
  while :; do
    local choice
    choice="$(ui_menu "Server service" "Service is currently: $(rr_service_active && echo RUNNING || echo STOPPED)" \
      start   "Start the server" \
      stop    "Stop the server" \
      restart "Restart the server" \
      enable  "Start automatically at boot" \
      disable "Do not start at boot" \
      back    "Back")" || return 0
    case "$choice" in
      start|stop|restart|enable|disable) svc "$choice" ;;
      back|"") return 0 ;;
    esac
    [ "$RR_INTERACTIVE" = 1 ] || return 0
  done
}

# --- update / maintenance -----------------------------------------------------
run_updater() {
  local updater="$BASE_INSTALL_DIR/update.sh"
  if [ ! -x "$updater" ]; then
    ui_error "Updater not found at $updater. Re-run the installer to restore it."
    return 1
  fi
  "$updater" "$@"
}

do_update() { run_updater "$@"; }

maintenance_menu() {
  while :; do
    local choice
    choice="$(ui_menu "Update & maintenance" "Installed server version: $(rr_binary_version "$SERVER_INSTALL_DIR/RemoteRelay.Server")" \
      stable    "Check for and install the latest stable release" \
      pre       "Install the latest pre-release" \
      repair    "Reinstall the current release (repair)" \
      uninstall "Uninstall RemoteRelay" \
      back      "Back")" || return 0
    case "$choice" in
      stable) run_updater ;;
      pre)    run_updater --pre-release ;;
      repair) run_updater --force ;;
      uninstall) do_uninstall ;;
      back|"") return 0 ;;
    esac
    [ "$RR_INTERACTIVE" = 1 ] || return 0
  done
}

# --- configuration ------------------------------------------------------------
cfg_relay_driver() {
  local cur; cur="$(rr_json_get "$SERVER_CONFIG" '.RelayDriver')"; cur="${cur:-Auto}"
  local pick
  pick="$(ui_radiolist "Relay driver" "Which relay backend should the server use?" "$cur" \
    Auto    "Auto-detect (GPIO on a Pi, otherwise mock)" \
    RpiGpio "Raspberry Pi GPIO relay HAT" \
    K8090   "Velleman K8090 USB relay board" \
    Mock    "Mock driver (no hardware, for testing)")" || return 0
  [ -z "$pick" ] && return 0

  if [ "$pick" = "K8090" ]; then
    local -a opts=()
    local p
    for p in /dev/serial/by-id/* /dev/ttyACM* /dev/ttyUSB*; do
      [ -e "$p" ] && opts+=("$p" "serial port")
    done
    opts+=("__manual__" "Enter a path manually")
    local port
    port="$(ui_menu "K8090 serial port" "Select the K8090 device:" "${opts[@]}")" || return 0
    if [ "$port" = "__manual__" ] || [ -z "$port" ]; then
      local existing; existing="$(rr_json_get "$SERVER_CONFIG" '.K8090.Port')"
      port="$(ui_inputbox "K8090 serial port" "Device path (e.g. /dev/ttyACM0):" "${existing:-/dev/ttyACM0}")"
    fi
    [ -z "$port" ] && { ui_warn "No port given; aborting."; return 0; }
    if rr_json_set "$SERVER_CONFIG" '.RelayDriver=$d | .K8090=((.K8090 // {}) + {Port:$p})' --arg d "K8090" --arg p "$port"; then
      ui_ok "Relay driver set to K8090 on $port."
    fi
  else
    rr_json_set "$SERVER_CONFIG" '.RelayDriver=$d' --arg d "$pick" && ui_ok "Relay driver set to $pick."
  fi
  restart_if_running
}

cfg_udp_api() {
  local cur; cur="$(rr_json_get "$SERVER_CONFIG" '.UdpApiPort')"
  local action
  action="$(ui_menu "UDP control API" "Currently: ${cur:-disabled}" \
    set     "Enable / set the UDP API port" \
    disable "Disable the UDP API" \
    back    "Back")" || return 0
  case "$action" in
    set)
      local port; port="$(ui_inputbox "UDP control API" "UDP port to listen on:" "${cur:-33102}")"
      if [[ "$port" =~ ^[0-9]+$ ]] && [ "$port" -ge 1 ] && [ "$port" -le 65535 ]; then
        rr_json_set "$SERVER_CONFIG" '.UdpApiPort=($p|tonumber)' --arg p "$port" && ui_ok "UDP API port set to $port."
        restart_if_running
      else
        ui_warn "Invalid port; no change made."
      fi
      ;;
    disable)
      rr_json_set "$SERVER_CONFIG" 'del(.UdpApiPort)' && ui_ok "UDP API disabled."
      restart_if_running
      ;;
  esac
}

cfg_default_source() {
  local -a opts=("__none__" "No default (clients choose)")
  local s
  while IFS= read -r s; do
    [ -n "$s" ] && opts+=("$s" "Route automatically on idle")
  done < <(rr_json_get "$SERVER_CONFIG" '.Routes[].SourceName' | sort -u)
  if [ "${#opts[@]}" -eq 2 ]; then
    ui_msgbox "Default source" "No sources are configured yet. Add sources from the client's Setup screen first."
    return 0
  fi
  local cur; cur="$(rr_json_get "$SERVER_CONFIG" '.DefaultSource')"
  local pick; pick="$(ui_menu "Default source" "Source selected automatically when idle (current: ${cur:-none}):" "${opts[@]}")" || return 0
  [ -z "$pick" ] && return 0
  if [ "$pick" = "__none__" ]; then
    rr_json_set "$SERVER_CONFIG" 'del(.DefaultSource)' && ui_ok "Default source cleared."
  else
    rr_json_set "$SERVER_CONFIG" '.DefaultSource=$s' --arg s "$pick" && ui_ok "Default source set to $pick."
  fi
  restart_if_running
}

cfg_client_connection() {
  [ -f "$CLIENT_CONFIG" ] || { ui_msgbox "Client connection" "The client is not installed on this machine."; return 0; }
  local host; host="$(rr_json_get "$CLIENT_CONFIG" '.Host')"
  local mode
  mode="$(ui_menu "Client connection" "How should this client find the server? (current host: ${host:-auto-discover})" \
    discover "Auto-discover the server on the network" \
    fixed    "Connect to a specific IP / hostname" \
    back     "Back")" || return 0
  case "$mode" in
    discover)
      rr_json_set "$CLIENT_CONFIG" 'del(.Host) | del(.Port)' && ui_ok "Client set to auto-discovery."
      ;;
    fixed)
      local newhost newport curport
      newhost="$(ui_inputbox "Server address" "Server IP or hostname:" "${host:-}")"
      [ -z "$newhost" ] && { ui_warn "No address entered; no change made."; return 0; }
      curport="$(rr_json_get "$CLIENT_CONFIG" '.Port')"
      newport="$(ui_inputbox "Server port" "Server port:" "${curport:-33101}")"
      [[ "$newport" =~ ^[0-9]+$ ]] || newport=33101
      rr_json_set "$CLIENT_CONFIG" '.Host=$h | .Port=($p|tonumber)' --arg h "$newhost" --arg p "$newport" \
        && ui_ok "Client will connect to $newhost:$newport."
      ;;
  esac
}

cfg_kiosk() {
  local wf="$USER_HOME/.config/wayfire.ini"
  local state="off"
  [ -f "$wf" ] && grep -Eq '^[ \t]*dpms_timeout[ \t]*=[ \t]*0' "$wf" 2>/dev/null && state="on"
  local choice
  choice="$(ui_menu "Screen blanking" "Currently: $( [ "$state" = on ] && echo "blanking DISABLED (kiosk)" || echo "default blanking" )" \
    disable "Disable screen blanking (kiosk display always on)" \
    enable  "Restore default screen blanking" \
    back    "Back")" || return 0
  mkdir -p "$USER_HOME/.config"
  [ -f "$wf" ] || { : > "$wf"; chown "$APP_USER:$APP_USER" "$wf"; }
  case "$choice" in
    disable) wayfire_set_idle "$wf" 0; ui_ok "Screen blanking disabled." ;;
    enable)  wayfire_remove_idle "$wf"; ui_ok "Default screen blanking restored." ;;
    *) return 0 ;;
  esac
}

# Set [idle] dpms/screensaver timeouts (mirrors install.sh awk logic).
wayfire_set_idle() {
  local wf="$1" val="$2" tmp="$1.tmp.$$"
  awk -v val="$val" '
    BEGIN { in_idle=0; seen=0; wd=0; ws=0 }
    /^[ \t]*\[idle\][ \t]*$/ { if(in_idle){ if(!wd)print "dpms_timeout = "val; if(!ws)print "screensaver_timeout = "val } print; in_idle=1; seen=1; wd=0; ws=0; next }
    /^[ \t]*\[/ { if(in_idle){ if(!wd)print "dpms_timeout = "val; if(!ws)print "screensaver_timeout = "val } in_idle=0; print; next }
    { if(in_idle){ if($0 ~ /^[ \t]*dpms_timeout[ \t]*=/){ if(!wd){print "dpms_timeout = "val; wd=1} next } if($0 ~ /^[ \t]*screensaver_timeout[ \t]*=/){ if(!ws){print "screensaver_timeout = "val; ws=1} next } } print }
    END { if(in_idle){ if(!wd)print "dpms_timeout = "val; if(!ws)print "screensaver_timeout = "val } else if(!seen){ print "[idle]"; print "dpms_timeout = "val; print "screensaver_timeout = "val } }
  ' "$wf" > "$tmp" && mv "$tmp" "$wf"
  chown "$APP_USER:$APP_USER" "$wf" 2>/dev/null || true
}

wayfire_remove_idle() {
  local wf="$1" tmp="$1.tmp.$$"
  awk '
    BEGIN { in_idle=0 }
    /^[ \t]*\[idle\][ \t]*$/ { in_idle=1; print; next }
    /^[ \t]*\[/ { in_idle=0; print; next }
    { if(in_idle && $0 ~ /^[ \t]*(dpms_timeout|screensaver_timeout)[ \t]*=[ \t]*0[ \t]*$/) next; print }
  ' "$wf" > "$tmp" && mv "$tmp" "$wf"
  chown "$APP_USER:$APP_USER" "$wf" 2>/dev/null || true
}

cfg_ntp() {
  local cur=""
  [ -f /etc/systemd/timesyncd.conf ] && cur="$(awk -F= '/^[ \t]*NTP[ \t]*=/{print $2}' /etc/systemd/timesyncd.conf | head -n1 | xargs)"
  local servers
  servers="$(ui_inputbox "NTP servers" "Space-separated NTP servers (blank to keep current):" "$cur")"
  if [ -z "$servers" ]; then ui_info "No change made."; return 0; fi
  [ -f /etc/systemd/timesyncd.conf ] && cp /etc/systemd/timesyncd.conf /etc/systemd/timesyncd.conf.bak
  printf '[Time]\nNTP=%s\n' "$servers" > /etc/systemd/timesyncd.conf
  systemctl restart systemd-timesyncd 2>/dev/null || true
  ui_ok "NTP servers set: $servers"
}

config_menu() {
  while :; do
    local choice
    choice="$(ui_menu "Configure RemoteRelay" "Options not available from the client app (routes & sources are edited there):" \
      driver  "Relay driver / K8090 serial port" \
      udp     "UDP control API port" \
      source  "Default source on idle" \
      client  "Client connection (auto-discover / fixed)" \
      kiosk   "Screen blanking (kiosk display)" \
      ntp     "NTP time servers" \
      back    "Back")" || return 0
    case "$choice" in
      driver) cfg_relay_driver ;;
      udp)    cfg_udp_api ;;
      source) cfg_default_source ;;
      client) cfg_client_connection ;;
      kiosk)  cfg_kiosk ;;
      ntp)    cfg_ntp ;;
      back|"") return 0 ;;
    esac
    [ "$RR_INTERACTIVE" = 1 ] || return 0
  done
}

# --- logs ---------------------------------------------------------------------
do_logs() {
  while :; do
    local choice
    choice="$(ui_menu "Logs" "Which log would you like to view?" \
      server  "Recent server log (journalctl)" \
      follow  "Follow the server log live" \
      client  "Client log file" \
      errlog  "Server error log" \
      back    "Back")" || return 0
    case "$choice" in
      server)
        local tmp; tmp="$(mktemp)"
        journalctl -u "$RR_SERVER_SERVICE_NAME" -n 400 --no-pager > "$tmp" 2>&1 || echo "No journal entries." > "$tmp"
        ui_textbox "Server log (last 400 lines)" "$tmp"; rm -f "$tmp"
        ;;
      follow)
        if [ "$RR_INTERACTIVE" = 1 ]; then
          ui_info "Following log — press Ctrl-C to stop."
          journalctl -u "$RR_SERVER_SERVICE_NAME" -f </dev/tty || true
        fi
        ;;
      client)  ui_textbox "Client log" "$CLIENT_INSTALL_DIR/client.log" ;;
      errlog)  ui_textbox "Server error log" "$SERVER_INSTALL_DIR/server_error.log" ;;
      back|"") return 0 ;;
    esac
    [ "$RR_INTERACTIVE" = 1 ] || return 0
  done
}

# --- uninstall ----------------------------------------------------------------
do_uninstall() {
  local uninst="$BASE_INSTALL_DIR/uninstall.sh"
  if [ -x "$uninst" ]; then
    "$uninst"
  else
    ui_error "Uninstaller not found at $uninst."
    return 1
  fi
}

# --- main menu ----------------------------------------------------------------
main_menu() {
  while :; do
    local svc_line="not installed"
    if [ -f "$RR_SERVER_SERVICE_FILE" ]; then
      svc_line="$(rr_service_active && echo "running" || echo "stopped")"
    fi
    local choice
    choice="$(ui_menu "RemoteRelay" "Server: $svc_line   ·   $BASE_INSTALL_DIR" \
      status  "Show status" \
      service "Start / stop / restart the server" \
      config  "Configure options" \
      update  "Update & maintenance" \
      logs    "View logs" \
      quit    "Quit")" || return 0
    case "$choice" in
      status)  do_status ;;
      service) service_menu ;;
      config)  config_menu ;;
      update)  maintenance_menu ;;
      logs)    do_logs ;;
      quit|"") return 0 ;;
    esac
  done
}

usage() {
  cat <<EOF
remoterelay $VERSION — manage an installed RemoteRelay system

Usage: sudo remoterelay [command]

Commands:
  (none)              open the interactive menu
  status              print system status
  start|stop|restart  control the server service
  enable|disable      control start-at-boot
  update [--pre-release|--force]
  config              open the configuration menu
  logs                view server logs
  uninstall           remove RemoteRelay
  version             print this tool's version
EOF
}

# --- dispatch -----------------------------------------------------------------
cmd="${1:-menu}"
case "$cmd" in
  version|-v|--version) echo "remoterelay $VERSION"; exit 0 ;;
  help|-h|--help)       usage; exit 0 ;;
esac

require_root "$cmd"
load_install

case "$cmd" in
  menu)                              main_menu ;;
  status)                            do_status ;;
  start|stop|restart|enable|disable) svc "$cmd" ;;
  service)                           service_menu ;;
  update)                            shift; do_update "$@" ;;
  config|configure)                  config_menu ;;
  logs|log)                          do_logs ;;
  uninstall)                         do_uninstall ;;
  *) ui_error "Unknown command: $cmd"; usage; exit 1 ;;
esac
