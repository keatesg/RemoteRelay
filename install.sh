#!/bin/bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
INSTALLER_VERSION="1.0.0"

# Display version for support purposes
echo "RemoteRelay Installer v${INSTALLER_VERSION}"
echo ""

if [ "$EUID" -ne 0 ]; then
  echo "Error: This installer must be run as root (sudo)." >&2
  exit 1
fi

# Trap handler for cleanup on failure
CLEANUP_NEEDED=false
cleanup_on_failure() {
  if [ "$CLEANUP_NEEDED" = true ]; then
    echo ""
    echo "Installation failed. Any partial changes have been preserved." >&2
    echo "Check the error messages above for details." >&2
  fi
}
trap cleanup_on_failure EXIT

# Check disk space (require at least 200MB free)
check_disk_space() {
  local required_mb=200
  local available_kb
  available_kb=$(df -k "${1:-/}" | awk 'NR==2 {print $4}')
  local available_mb=$((available_kb / 1024))
  
  if [ "$available_mb" -lt "$required_mb" ]; then
    echo "Error: Insufficient disk space. Required: ${required_mb}MB, Available: ${available_mb}MB" >&2
    exit 1
  fi
  echo "Disk space check passed (${available_mb}MB available)"
}

# Detect and validate architecture
detect_architecture() {
  local arch
  arch=$(uname -m)
  local installer_arch=""
  
  # Check if 'file' command is available for architecture detection
  if ! command -v file >/dev/null 2>&1; then
    echo "Note: 'file' command not found, skipping architecture validation"
    echo "Architecture: $arch"
    return 0
  fi
  
  # Determine what architecture this installer was built for
  if [ -d "$SCRIPT_DIR/server_files" ]; then
    # Check the binary to see what it was compiled for
    if file "$SCRIPT_DIR/server_files/RemoteRelay.Server" 2>/dev/null | grep -q "ARM aarch64"; then
      installer_arch="aarch64"
    elif file "$SCRIPT_DIR/server_files/RemoteRelay.Server" 2>/dev/null | grep -q "ARM,"; then
      installer_arch="armv7l"
    elif file "$SCRIPT_DIR/server_files/RemoteRelay.Server" 2>/dev/null | grep -q "x86-64"; then
      installer_arch="x86_64"
    fi
  fi
  
  if [ -n "$installer_arch" ] && [ "$arch" != "$installer_arch" ]; then
    echo "Warning: This installer was built for $installer_arch but you are running on $arch." >&2
    echo "The application may not work correctly." >&2
    if ! prompt_yes_no "Continue anyway? [y/N] " "N"; then
      exit 1
    fi
  else
    echo "Architecture: $arch"
  fi
}

install_dependencies() {
  if command -v apt-get >/dev/null 2>&1; then
    echo "Checking dependencies..."
    if ! command -v curl >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
       echo "Installing dependencies (curl, jq)..."
       apt-get update -qq && apt-get install -y -qq curl jq
    fi
  else
    echo "Warning: apt-get not found. Please ensure curl and jq are installed manually."
  fi
}

install_dependencies

APP_USER="${SUDO_USER:-}"
if [ -z "$APP_USER" ] || [ "$APP_USER" = "root" ]; then
  echo "Error: Unable to determine the non-root user running sudo." >&2
  echo "Re-run using: sudo -E ./install.sh" >&2
  exit 1
fi

USER_HOME=$(eval echo ~$APP_USER)
if [ -z "$USER_HOME" ] || [ ! -d "$USER_HOME" ]; then
  echo "Error: Could not resolve home directory for $APP_USER." >&2
  exit 1
fi

# Run pre-flight checks
check_disk_space "$USER_HOME"

BASE_INSTALL_DIR="$USER_HOME/RemoteRelay"
SERVER_INSTALL_DIR="$BASE_INSTALL_DIR/server"
CLIENT_INSTALL_DIR="$BASE_INSTALL_DIR/client"
UNINSTALL_SCRIPT_SOURCE="uninstall.sh"
UNINSTALL_SCRIPT_DEST="$USER_HOME/RemoteRelay/uninstall.sh"
SERVER_FILES_SOURCE_DIR="server_files"
CLIENT_FILES_SOURCE_DIR="client_files"
LEGACY_BASE_DIR="$USER_HOME/.local/share/RemoteRelay"
SERVER_SERVICE_NAME="remote-relay-server.service"
SERVER_SERVICE_FILE="/etc/systemd/system/$SERVER_SERVICE_NAME"

SUMMARY=()

bold() { printf "\033[1m%s\033[0m" "$1"; }

prompt_yes_no() {
  local prompt="$1"
  local default="$2"
  local answer
  while true; do
    read -r -p "$prompt" answer || answer=""
    answer=${answer:-$default}
    case "${answer^^}" in
      Y|YES) return 0 ;;
      N|NO) return 1 ;;
    esac
    echo "Please answer yes or no (y/n)."
  done
}

# Run architecture check now that prompt_yes_no is defined
detect_architecture

ensure_dir_owned_by_user() {
  local dir="$1"
  mkdir -p "$dir"
  chown -R "$APP_USER:$APP_USER" "$dir"
}

preserve_file_if_exists() {
  local file_path="$1"
  local tmp_var="$2"
  if [ -f "$file_path" ]; then
    local backup_dir="$USER_HOME/.remoterelay-backup-$$"
    mkdir -p "$backup_dir"
    local filename=$(basename "$file_path")
    local backup_path="$backup_dir/$filename"
    if cp "$file_path" "$backup_path"; then
      echo "  Preserving existing config: $filename"
      printf -v "$tmp_var" '%s' "$backup_path"
    else
      echo "  Warning: Failed to preserve $filename" >&2
      printf -v "$tmp_var" '%s' ""
    fi
  else
    printf -v "$tmp_var" '%s' ""
  fi
}

restore_preserved_file() {
  local backup_path="$1"
  local dest_path="$2"
  if [ -n "$backup_path" ] && [ -f "$backup_path" ]; then
    local filename=$(basename "$dest_path")
    if cp "$backup_path" "$dest_path"; then
      echo "  Restored existing config: $filename"
      chown "$APP_USER:$APP_USER" "$dest_path"
    else
      echo "  Warning: Failed to restore $filename" >&2
    fi
  fi
}

cleanup_backup_dir() {
  local backup_dir="$USER_HOME/.remoterelay-backup-$$"
  if [ -d "$backup_dir" ]; then
    rm -rf "$backup_dir"
  fi
}

stop_server_if_running() {
  if systemctl is-active --quiet "$SERVER_SERVICE_NAME"; then
    echo "Stopping existing $SERVER_SERVICE_NAME..."
    systemctl stop "$SERVER_SERVICE_NAME"
    return 0
  fi
  return 1
}

start_server_service() {
  echo "Starting $SERVER_SERVICE_NAME..."
  systemctl start "$SERVER_SERVICE_NAME" || true
  if systemctl is-active --quiet "$SERVER_SERVICE_NAME"; then
    echo "Server service is running."
  else
    echo "Warning: Server service failed to start. Check with 'journalctl -u $SERVER_SERVICE_NAME'."
  fi
}

install_systemd_service_for_server() {
  local inactive_pin="$1"
  local inactive_state="$2"

  cat > "$SERVER_SERVICE_FILE" <<EOF
[Unit]
Description=RemoteRelay Server
After=network.target

[Service]
User=$APP_USER
Group=$APP_USER
WorkingDirectory=$SERVER_INSTALL_DIR
ExecStart=$SERVER_INSTALL_DIR/RemoteRelay.Server
Restart=always
RestartSec=5
EOF

  if [ -n "$inactive_pin" ] && [ -n "$inactive_state" ]; then
    echo "ExecStartPre=$SERVER_INSTALL_DIR/RemoteRelay.Server set-inactive-relay --pin $inactive_pin --state $inactive_state" >> "$SERVER_SERVICE_FILE"
    echo "ExecStopPost=$SERVER_INSTALL_DIR/RemoteRelay.Server set-inactive-relay --pin $inactive_pin --state $inactive_state" >> "$SERVER_SERVICE_FILE"
  fi

  cat >> "$SERVER_SERVICE_FILE" <<'EOF'

[Install]
WantedBy=multi-user.target
EOF

  chmod 644 "$SERVER_SERVICE_FILE"
  systemctl daemon-reload
  systemctl enable "$SERVER_SERVICE_NAME"
}

clean_wayfire_duplicates() {
  local wayfire_ini="$1"
  if [ ! -f "$wayfire_ini" ]; then
    return
  fi
  
  local tmp="$wayfire_ini.clean.$$"
  
  # Remove all existing remote-relay-client entries and duplicate idle settings
  awk '
    BEGIN { in_autostart = 0; in_idle = 0; }
    /^[ \t]*\[autostart\][ \t]*$/ {
      in_autostart = 1; in_idle = 0; print; next;
    }
    /^[ \t]*\[idle\][ \t]*$/ {
      in_idle = 1; in_autostart = 0; print; next;
    }
    /^[ \t]*\[/ {
      in_autostart = 0; in_idle = 0; print; next;
    }
    {
      if (in_autostart && $0 ~ /^[ \t]*remote-relay-client[ \t]*=/) {
        next;
      }
      if (in_idle && ($0 ~ /^[ \t]*dpms_timeout[ \t]*=/ || $0 ~ /^[ \t]*screensaver_timeout[ \t]*=/)) {
        next;
      }
      print;
    }
  ' "$wayfire_ini" > "$tmp"
  
  if [ -s "$tmp" ]; then
    mv "$tmp" "$wayfire_ini"
    chown "$APP_USER:$APP_USER" "$wayfire_ini"
    echo "Cleaned duplicate entries from wayfire.ini"
  else
    echo "Warning: Failed to clean wayfire.ini" >&2
    rm -f "$tmp"
  fi
}

configure_wayfire_idle() {
  local wayfire_ini="$1"
  local tmp="$wayfire_ini.tmp.$$"

  awk '
    BEGIN {
      in_idle = 0; seen_idle = 0; wrote_dpms = 0; wrote_screen = 0;
    }
    /^[ \t]*\[idle\][ \t]*$/ {
      if (in_idle) {
        if (!wrote_dpms) print "dpms_timeout = 0";
        if (!wrote_screen) print "screensaver_timeout = 0";
      }
      print; in_idle = 1; seen_idle = 1; wrote_dpms = 0; wrote_screen = 0; next;
    }
    /^[ \t]*\[/ {
      if (in_idle) {
        if (!wrote_dpms) print "dpms_timeout = 0";
        if (!wrote_screen) print "screensaver_timeout = 0";
      }
      in_idle = 0;
      print; next;
    }
    {
      if (in_idle) {
        if ($0 ~ /^[ \t]*dpms_timeout[ \t]*=/) {
          if (!wrote_dpms) {
            print "dpms_timeout = 0"; wrote_dpms = 1;
          }
          next;
        }
        if ($0 ~ /^[ \t]*screensaver_timeout[ \t]*=/) {
          if (!wrote_screen) {
            print "screensaver_timeout = 0"; wrote_screen = 1;
          }
          next;
        }
      }
      print;
    }
    END {
      if (in_idle) {
        if (!wrote_dpms) print "dpms_timeout = 0";
        if (!wrote_screen) print "screensaver_timeout = 0";
      } else if (!seen_idle) {
        print "[idle]";
        print "dpms_timeout = 0";
        print "screensaver_timeout = 0";
      }
    }
  ' "$wayfire_ini" > "$tmp" && mv "$tmp" "$wayfire_ini"
}

ensure_wayfire_autostart() {
  local wayfire_ini="$1"
  local command_line="$2"
  local tmp="$wayfire_ini.autostart.$$"

  awk -v entry="$command_line" '
    BEGIN { in_autostart = 0; seen_autostart = 0; wrote_entry = 0; key = "remote-relay-client"; }
    /^[ \t]*\[autostart\][ \t]*$/ {
      if (in_autostart && !wrote_entry) print key " = " entry;
      print; in_autostart = 1; seen_autostart = 1; wrote_entry = 0; next;
    }
    /^[ \t]*\[/ {
      if (in_autostart && !wrote_entry) print key " = " entry;
      in_autostart = 0;
      print; next;
    }
    {
      if (in_autostart) {
        if ($0 ~ "^[ \\t]*" key "[ \\t]*=") {
          if (!wrote_entry) {
            print key " = " entry;
            wrote_entry = 1;
          }
          next;
        }
      }
      print;
    }
    END {
      if (in_autostart && !wrote_entry) {
        print key " = " entry;
      } else if (!seen_autostart) {
        print "[autostart]";
        print key " = " entry;
      }
    }
  ' "$wayfire_ini" > "$tmp" && mv "$tmp" "$wayfire_ini"
}

configure_wayfire() {
  local launcher_script="$1"
  local disable_idle="${2:-false}"
  local wayfire_ini="$USER_HOME/.config/wayfire.ini"
  ensure_dir_owned_by_user "$USER_HOME/.config"
  if [ ! -f "$wayfire_ini" ]; then
    touch "$wayfire_ini"
    chown "$APP_USER:$APP_USER" "$wayfire_ini"
  fi

  # Clean up any duplicate entries from previous installs
  clean_wayfire_duplicates "$wayfire_ini"

  # Build the command to run
  # Wayfire runs commands with sh, so just call the launcher script directly
  # Screen blanking is handled by [idle] section, not xset
  local autostart_cmd="$launcher_script"

  ensure_wayfire_autostart "$wayfire_ini" "$autostart_cmd"
  if [ "$disable_idle" = "true" ]; then
    configure_wayfire_idle "$wayfire_ini"
  fi
  chown "$APP_USER:$APP_USER" "$wayfire_ini"
  echo "Wayfire autostart configured in $wayfire_ini"
}

create_autostart_desktop() {
  local launcher_script="$1"
  local enable_kiosk="$2"
  local desktop_path="$USER_HOME/.config/autostart/remote-relay-client.desktop"
  
  # Note: This XDG autostart is for legacy X11 compatibility only.
  # Wayfire (default on modern RPi OS) uses wayfire.ini [autostart] instead.
  
  # Ensure directories exist with proper permissions
  ensure_dir_owned_by_user "$USER_HOME/.config"
  ensure_dir_owned_by_user "$USER_HOME/.config/autostart"

  local exec_line
  if [ "$enable_kiosk" = "true" ]; then
    # For XDG autostart, use simple format - no nested quotes
    exec_line="sh -c 'xset s noblank; xset s off; xset -dpms; exec $launcher_script'"
  else
    exec_line="$launcher_script"
  fi

  cat > "$desktop_path" <<EOF
[Desktop Entry]
Type=Application
Name=RemoteRelay Client
Exec=$exec_line
Path=$CLIENT_INSTALL_DIR
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
  chmod 644 "$desktop_path"
  chown "$APP_USER:$APP_USER" "$desktop_path"

  echo "XDG autostart desktop entry created (for X11 compatibility)"
}

create_client_launcher() {
  local client_exec="$1"
  local launcher_script="$CLIENT_INSTALL_DIR/start-client.sh"
  
  cat > "$launcher_script" <<'EOF'
#!/bin/bash
# RemoteRelay Client Launcher Script
# Sets up environment and launches the client with watchdog restart

# Configuration
MAX_RESTART_ATTEMPTS=5
RESTART_DELAY=5
DISPLAY_WAIT_TIMEOUT=30

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Change to the client directory
cd "$SCRIPT_DIR" || exit 1

# Check for ARMv8.1+ features (like lse) to handle older CPUs (e.g. Pi 3)
# If 'lse' is missing from cpuinfo, disable .NET use of LSE instructions/intrinsics
if [ -f /proc/cpuinfo ]; then
  if ! grep -q "lse" /proc/cpuinfo && ! grep -q "atomics" /proc/cpuinfo; then
     echo "Detected CPU missing LSE/Atomics (likely Pi 3). Disabling DOTNET_EnableHWIntrinsic and DOTNET_EnableLse."
     export DOTNET_EnableHWIntrinsic=0
     export DOTNET_EnableLse=0
  fi
fi

# Enable logging for debugging autostart issues
exec >> "$SCRIPT_DIR/client.log" 2>&1
echo ""
echo "=========================================="
echo "[$(date)] Starting RemoteRelay Client Launcher"
echo "Working directory: $(pwd)"
echo "Script directory: $SCRIPT_DIR"
echo "Display: $DISPLAY"
echo "Wayland display: $WAYLAND_DISPLAY"
echo "XDG_SESSION_TYPE: $XDG_SESSION_TYPE"

# Wait for display server to be ready
wait_for_display() {
    local waited=0
    echo "[$(date)] Waiting for display server..."
    
    while [ $waited -lt $DISPLAY_WAIT_TIMEOUT ]; do
        # Check for Wayland
        if [ -n "$WAYLAND_DISPLAY" ] && [ -e "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" ]; then
            echo "[$(date)] Wayland display ready"
            return 0
        fi
        
        # Check for X11
        if [ -n "$DISPLAY" ]; then
            if command -v xdpyinfo >/dev/null 2>&1 && xdpyinfo >/dev/null 2>&1; then
                echo "[$(date)] X11 display ready"
                return 0
            fi
        fi
        
        sleep 1
        waited=$((waited + 1))
    done
    
    echo "[$(date)] Warning: Display server check timed out, attempting to continue anyway"
    return 1
}

# Wait a bit for the desktop environment to fully initialize, then check display
sleep 3
wait_for_display

# Watchdog restart loop
restart_count=0

while [ $restart_count -lt $MAX_RESTART_ATTEMPTS ]; do
    echo "[$(date)] Launching RemoteRelay binary (attempt $((restart_count + 1))/$MAX_RESTART_ATTEMPTS)"
    
    "$SCRIPT_DIR/RemoteRelay"
    exit_code=$?
    
    if [ $exit_code -eq 0 ]; then
        echo "[$(date)] RemoteRelay exited normally"
        break
    fi
    
    restart_count=$((restart_count + 1))
    echo "[$(date)] RemoteRelay exited with code $exit_code"
    
    if [ $restart_count -lt $MAX_RESTART_ATTEMPTS ]; then
        echo "[$(date)] Restarting in $RESTART_DELAY seconds..."
        sleep $RESTART_DELAY
    else
        echo "[$(date)] Max restart attempts reached. Giving up."
    fi
done
EOF

  chmod +x "$launcher_script"
  chown "$APP_USER:$APP_USER" "$launcher_script"
  echo "$launcher_script"
}

create_desktop_shortcut() {
  local launcher_script="$1"
  local desktop_dir="$USER_HOME/Desktop"
  local desktop_shortcut="$desktop_dir/RemoteRelay.desktop"
  
  # Ensure Desktop directory exists
  if [ ! -d "$desktop_dir" ]; then
    ensure_dir_owned_by_user "$desktop_dir"
  fi

  cat > "$desktop_shortcut" <<EOF
[Desktop Entry]
Type=Application
Name=RemoteRelay Client
Comment=Launch RemoteRelay Client
Exec="$launcher_script"
Path=$CLIENT_INSTALL_DIR
Icon=applications-multimedia
Terminal=false
Categories=AudioVideo;Audio;
EOF
  chmod 755 "$desktop_shortcut"
  chown "$APP_USER:$APP_USER" "$desktop_shortcut"
}

migrate_legacy_layout() {
  if [ ! -d "$LEGACY_BASE_DIR" ]; then
    return
  fi

  echo "Legacy installation detected at $LEGACY_BASE_DIR. Migrating to $BASE_INSTALL_DIR..."
  ensure_dir_owned_by_user "$BASE_INSTALL_DIR"

  if [ -d "$LEGACY_BASE_DIR/Server" ]; then
    ensure_dir_owned_by_user "$SERVER_INSTALL_DIR"
    cp -a "$LEGACY_BASE_DIR/Server/." "$SERVER_INSTALL_DIR/"
  fi

  if [ -d "$LEGACY_BASE_DIR/Client" ]; then
    ensure_dir_owned_by_user "$CLIENT_INSTALL_DIR"
    cp -a "$LEGACY_BASE_DIR/Client/." "$CLIENT_INSTALL_DIR/"
  fi

  chown -R "$APP_USER:$APP_USER" "$BASE_INSTALL_DIR"

  local legacy_backup="$LEGACY_BASE_DIR-backup-$(date +%s)"
  mv "$LEGACY_BASE_DIR" "$legacy_backup"
  echo "Legacy files copied. Original kept at $legacy_backup"
  SUMMARY+=("Migrated legacy installation to $BASE_INSTALL_DIR")
}

update_server_files() {
  local server_restarted=0
  if [ ! -d "$SERVER_FILES_SOURCE_DIR" ]; then
    echo "Error: server payload missing." >&2
    exit 1
  fi

  ensure_dir_owned_by_user "$SERVER_INSTALL_DIR"

  echo "Preserving existing server configuration files..."
  local backup_config backup_appsettings backup_devsettings
  preserve_file_if_exists "$SERVER_INSTALL_DIR/config.json" backup_config
  preserve_file_if_exists "$SERVER_INSTALL_DIR/appsettings.json" backup_appsettings
  preserve_file_if_exists "$SERVER_INSTALL_DIR/appsettings.Development.json" backup_devsettings

  if stop_server_if_running; then
    server_restarted=1
  fi

  echo "Installing new server files..."
  find "$SERVER_INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  cp -a "$SERVER_FILES_SOURCE_DIR/." "$SERVER_INSTALL_DIR/"
  chmod +x "$SERVER_INSTALL_DIR/RemoteRelay.Server"

  echo "Restoring preserved configuration files..."
  restore_preserved_file "$backup_config" "$SERVER_INSTALL_DIR/config.json"
  restore_preserved_file "$backup_appsettings" "$SERVER_INSTALL_DIR/appsettings.json"
  restore_preserved_file "$backup_devsettings" "$SERVER_INSTALL_DIR/appsettings.Development.json"

  chown -R "$APP_USER:$APP_USER" "$SERVER_INSTALL_DIR"

  local inactive_pin="" inactive_state=""
  if command -v jq >/dev/null 2>&1; then
    if [ -f "$SERVER_INSTALL_DIR/config.json" ]; then
      inactive_pin=$(jq -r '.InactiveRelay.Pin // empty' "$SERVER_INSTALL_DIR/config.json") || inactive_pin=""
      inactive_state=$(jq -r '.InactiveRelay.InactiveState // empty' "$SERVER_INSTALL_DIR/config.json") || inactive_state=""
      if [ -n "$inactive_state" ]; then
        inactive_state="$(echo "$inactive_state" | awk '{print toupper(substr($0,1,1)) tolower(substr($0,2))}')"
      fi
      if ! [[ "$inactive_pin" =~ ^[0-9]+$ ]]; then
        inactive_pin=""; inactive_state=""
      fi
      case "$inactive_state" in
        High|Low) ;;
        *) inactive_state="" ;;
      esac
    fi
  else
    echo "Note: jq not found. Skipping inactive relay helpers."
  fi

  install_systemd_service_for_server "$inactive_pin" "$inactive_state"

  if [ "$server_restarted" -eq 1 ]; then
    start_server_service
  else
    systemctl restart "$SERVER_SERVICE_NAME" >/dev/null 2>&1 || start_server_service
  fi

  SUMMARY+=("Server files installed at $SERVER_INSTALL_DIR")
}

update_client_files() {
  if [ ! -d "$CLIENT_FILES_SOURCE_DIR" ]; then
    echo "Error: client payload missing." >&2
    exit 1
  fi

  ensure_dir_owned_by_user "$CLIENT_INSTALL_DIR"

  echo "Preserving existing client configuration files..."
  local backup_server_details backup_client_config
  preserve_file_if_exists "$CLIENT_INSTALL_DIR/ServerDetails.json" backup_server_details
  preserve_file_if_exists "$CLIENT_INSTALL_DIR/ClientConfig.json" backup_client_config

  echo "Stopping existing client if running..."
  pkill -f "start-client.sh" || true
  pkill -x "RemoteRelay" || true

  echo "Installing new client files..."
  find "$CLIENT_INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  cp -a "$CLIENT_FILES_SOURCE_DIR/." "$CLIENT_INSTALL_DIR/"
  chmod +x "$CLIENT_INSTALL_DIR/RemoteRelay"

  echo "Restoring preserved configuration files..."
  restore_preserved_file "$backup_server_details" "$CLIENT_INSTALL_DIR/ServerDetails.json"
  restore_preserved_file "$backup_client_config" "$CLIENT_INSTALL_DIR/ClientConfig.json"
  
  # Ensure IsFullscreen is set to true in ClientConfig.json by default
  local client_config_path="$CLIENT_INSTALL_DIR/ClientConfig.json"
  if [ -f "$client_config_path" ]; then
    if command -v jq >/dev/null 2>&1; then
      local tmp
      tmp=$(mktemp)
      jq '.IsFullscreen = true' "$client_config_path" > "$tmp" && mv "$tmp" "$client_config_path"
    fi
  else
    echo '{"IsFullscreen": true}' > "$client_config_path"
  fi
  
  chown -R "$APP_USER:$APP_USER" "$CLIENT_INSTALL_DIR"

  local enable_kiosk="false"
  if prompt_yes_no "Disable screen blanking for the client display? [Y/n] " "Y"; then
    enable_kiosk="true"
  fi

  echo "Creating launcher script..."
  local launcher_script
  launcher_script=$(create_client_launcher "$CLIENT_INSTALL_DIR/RemoteRelay")

  echo "Configuring autostart..."
  # Wayfire (Wayland) is the default on modern RPi OS - configure it first
  configure_wayfire "$launcher_script" "$enable_kiosk"
  # Also create XDG autostart for X11 compatibility (legacy)
  create_autostart_desktop "$launcher_script" "$enable_kiosk"
  create_desktop_shortcut "$launcher_script"

  if [ "$enable_kiosk" = "true" ]; then
    echo "Kiosk mode enabled: display will stay awake."
  else
    echo "Kiosk mode skipped: screen blanking settings unchanged."
  fi

  echo "Re-launching client..."
  if [ -x "$CLIENT_INSTALL_DIR/start-client.sh" ]; then
    local current_user="${SUDO_USER:-$APP_USER}"
    if [ -n "$current_user" ]; then
      local user_id
      user_id=$(id -u "$current_user")
      su - "$current_user" -c "export XDG_RUNTIME_DIR=/run/user/$user_id; export WAYLAND_DISPLAY=wayland-1; export DISPLAY=:0; nohup $CLIENT_INSTALL_DIR/start-client.sh >/dev/null 2>&1 &"
    fi
  fi

  local client_summary="Client files installed at $CLIENT_INSTALL_DIR (autostart configured)"
  if [ "$enable_kiosk" = "true" ]; then
    client_summary+=" with kiosk screen blanking disabled"
  fi
  SUMMARY+=("$client_summary")
  SUMMARY+=("Desktop shortcut created at $USER_HOME/Desktop/RemoteRelay.desktop")
}

update_client_config_host() {
  local server_address="$1"
  local client_config="$CLIENT_INSTALL_DIR/ClientConfig.json"
  if [ -f "$client_config" ]; then
    local tmp
    tmp=$(mktemp)
    
    if command -v jq >/dev/null 2>&1; then
       if [ -z "$server_address" ]; then
           # Set Host to empty string or remove it for service discovery
           jq 'del(.Host) | del(.Port)' "$client_config" > "$tmp" && mv "$tmp" "$client_config"
           SUMMARY+=("Client configured to use Service Discovery")
       else
           jq --arg host "$server_address" '.Host = $host' "$client_config" > "$tmp" && mv "$tmp" "$client_config"
           SUMMARY+=("Updated client host to $server_address")
       fi
       chown "$APP_USER:$APP_USER" "$client_config"
    else
       # fallback to sed
       if [ -z "$server_address" ]; then
          if sed -E 's/("Host"\s*:\s*")[^"]*(")/\1\2/' "$client_config" > "$tmp"; then
            mv "$tmp" "$client_config"
            chown "$APP_USER:$APP_USER" "$client_config"
            SUMMARY+=("Client configured to use Service Discovery")
          else
            echo "Warning: failed to update $client_config" >&2
            rm -f "$tmp"
          fi
       else
          local escaped_address
          escaped_address=$(printf '%s' "$server_address" | sed -e 's/[\\/&]/\\&/g')
          if sed -E "s/(\"Host\"\s*:\s*\")([^\"]*)(\")/\\1$escaped_address\\3/" "$client_config" > "$tmp"; then
            mv "$tmp" "$client_config"
            chown "$APP_USER:$APP_USER" "$client_config"
            SUMMARY+=("Updated client host to $server_address")
          else
            echo "Warning: failed to update $client_config" >&2
            rm -f "$tmp"
          fi
       fi
    fi
  fi
}

copy_uninstall_script() {
  ensure_dir_owned_by_user "$BASE_INSTALL_DIR"
  if [ -f "$SCRIPT_DIR/$UNINSTALL_SCRIPT_SOURCE" ]; then
    cp "$SCRIPT_DIR/$UNINSTALL_SCRIPT_SOURCE" "$UNINSTALL_SCRIPT_DEST"
    chmod +x "$UNINSTALL_SCRIPT_DEST"
    chown "$APP_USER:$APP_USER" "$UNINSTALL_SCRIPT_DEST"
    SUMMARY+=("Uninstall script placed at $UNINSTALL_SCRIPT_DEST")
  else
    echo "Warning: uninstall script not found in installer payload." >&2
  fi
}

copy_update_script() {
  ensure_dir_owned_by_user "$BASE_INSTALL_DIR"
  if [ -f "$SCRIPT_DIR/update.sh" ]; then
    cp "$SCRIPT_DIR/update.sh" "$BASE_INSTALL_DIR/update.sh"
    chmod +x "$BASE_INSTALL_DIR/update.sh"
    chown "$APP_USER:$APP_USER" "$BASE_INSTALL_DIR/update.sh"
    SUMMARY+=("Update script installed at $BASE_INSTALL_DIR/update.sh")
  else
    echo "Warning: update script not found in installer payload." >&2
  fi
}

configure_ntp() {
  local ntp_servers="$1"
  if [ -z "$ntp_servers" ]; then
    return
  fi

  echo "Configuring NTP servers: $ntp_servers"

  # Back up existing config
  if [ -f /etc/systemd/timesyncd.conf ]; then
    cp /etc/systemd/timesyncd.conf /etc/systemd/timesyncd.conf.bak
    echo "  Backed up existing timesyncd.conf to timesyncd.conf.bak"
  fi

  # Write new timesyncd config
  cat > /etc/systemd/timesyncd.conf <<EOF
[Time]
NTP=$ntp_servers
EOF

  # Restart timesyncd to apply
  systemctl restart systemd-timesyncd 2>/dev/null || true
  echo "NTP servers configured and timesyncd restarted."
  SUMMARY+=("NTP servers configured: $ntp_servers")
}

echo "----------------------------------------------------"
echo "$(bold "RemoteRelay Installer")"
echo "----------------------------------------------------"
echo "Target user: $APP_USER"
echo "Install root: $BASE_INSTALL_DIR"
echo

migrate_legacy_layout

SERVER_ALREADY_PRESENT=false
CLIENT_ALREADY_PRESENT=false
if [ -d "$SERVER_INSTALL_DIR" ] && [ -f "$SERVER_INSTALL_DIR/RemoteRelay.Server" ]; then
  SERVER_ALREADY_PRESENT=true
fi
if [ -d "$CLIENT_INSTALL_DIR" ] && [ -f "$CLIENT_INSTALL_DIR/RemoteRelay" ]; then
  CLIENT_ALREADY_PRESENT=true
fi

DO_INSTALL_SERVER=false
DO_INSTALL_CLIENT=false

if $SERVER_ALREADY_PRESENT || $CLIENT_ALREADY_PRESENT; then
  echo "Existing installation detected."
  if $SERVER_ALREADY_PRESENT; then
    if prompt_yes_no "Update server component? [Y/n] " "Y"; then
      DO_INSTALL_SERVER=true
    fi
  else
    if prompt_yes_no "Install server component? [y/N] " "N"; then
      DO_INSTALL_SERVER=true
    fi
  fi

  if $CLIENT_ALREADY_PRESENT; then
    if prompt_yes_no "Update client component? [Y/n] " "Y"; then
      DO_INSTALL_CLIENT=true
    fi
  else
    if prompt_yes_no "Install client component? [y/N] " "N"; then
      DO_INSTALL_CLIENT=true
    fi
  fi
else
  if prompt_yes_no "Install server component? [Y/n] " "Y"; then
    DO_INSTALL_SERVER=true
  fi

  if prompt_yes_no "Install client component? [Y/n] " "Y"; then
    DO_INSTALL_CLIENT=true
  fi

  if ! $DO_INSTALL_SERVER && ! $DO_INSTALL_CLIENT; then
    echo "Nothing selected. Exiting."
    exit 0
  fi
fi

SERVER_ADDRESS=""
if $DO_INSTALL_CLIENT; then
  # Scenario 1: Installing BOTH Server and Client -> Default to localhost silently
  if $DO_INSTALL_SERVER; then
     echo "Installing both server and client: defaulting client to localhost."
     SERVER_ADDRESS="localhost"
  else
     # Scenario 2: Installing Client ONLY (or updating)
     # We still want to respect existing config if updating
     local_default=""
     
     if command -v jq >/dev/null 2>&1 && [ -f "$CLIENT_INSTALL_DIR/ClientConfig.json" ]; then
         existing_host=$(jq -r '.Host // empty' "$CLIENT_INSTALL_DIR/ClientConfig.json")
         if [ -n "$existing_host" ]; then
             local_default="$existing_host"
         fi
     fi

     echo "Configure Client Connection:"
     echo "  1) Use Service Discovery (Auto-detect server on network) [Default]"
     echo "  2) Specify Server IP/Hostname"
     read -r -p "Choice [1]: " conn_choice
     conn_choice=${conn_choice:-1}
     
     if [ "$conn_choice" = "2" ]; then
        if [ -n "$local_default" ]; then
            read -r -p "Enter Server IP/Hostname [$local_default]: " input_addr
            SERVER_ADDRESS=${input_addr:-$local_default}
        else
            read -r -p "Enter Server IP/Hostname: " input_addr
            SERVER_ADDRESS=${input_addr:-""}
        fi
     else
        SERVER_ADDRESS=""
     fi
  fi
fi

NTP_SERVERS=""
if prompt_yes_no "Configure custom NTP servers? [y/N] " "N"; then
  read -r -p "Enter NTP server addresses (space-separated): " NTP_SERVERS
  if [ -z "$NTP_SERVERS" ]; then
    echo "No NTP servers entered, skipping."
  fi
fi

# Mark that we're now modifying the system
CLEANUP_NEEDED=true

if $DO_INSTALL_SERVER; then
  update_server_files
fi

if $DO_INSTALL_CLIENT; then
  update_client_files
  update_client_config_host "$SERVER_ADDRESS"
fi

copy_uninstall_script
copy_update_script
configure_ntp "$NTP_SERVERS"

chown -R "$APP_USER:$APP_USER" "$BASE_INSTALL_DIR"

cleanup_backup_dir

# Verification step
echo ""
echo "Verifying installation..."
VERIFY_FAILED=false

if $DO_INSTALL_SERVER; then
  if [ -x "$SERVER_INSTALL_DIR/RemoteRelay.Server" ]; then
    echo "  ✓ Server binary is executable"
    
    # Check if service can be queried
    if systemctl is-enabled "$SERVER_SERVICE_NAME" >/dev/null 2>&1; then
      echo "  ✓ Server service is enabled"
    else
      echo "  ⚠ Server service may not be enabled properly"
    fi
  else
    echo "  ✗ Server binary is missing or not executable" >&2
    VERIFY_FAILED=true
  fi
fi

if $DO_INSTALL_CLIENT; then
  if [ -x "$CLIENT_INSTALL_DIR/RemoteRelay" ]; then
    echo "  ✓ Client binary is executable"
  else
    echo "  ✗ Client binary is missing or not executable" >&2
    VERIFY_FAILED=true
  fi
fi

if [ "$VERIFY_FAILED" = true ]; then
  echo ""
  echo "⚠ Installation completed with warnings. Check the messages above." >&2
else
  echo "  ✓ All verification checks passed"
fi

# Installation succeeded - disable cleanup trap
CLEANUP_NEEDED=false

echo
echo "----------------------------------------------------"
echo "Installation complete"
echo "----------------------------------------------------"
for line in "${SUMMARY[@]}"; do
  echo "- $line"
done

echo
echo "Server service: systemctl status $SERVER_SERVICE_NAME"
echo "Client binary: $CLIENT_INSTALL_DIR/RemoteRelay"
echo "Configuration lives in user-owned files at $BASE_INSTALL_DIR"

exit 0
