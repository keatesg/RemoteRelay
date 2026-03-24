#!/bin/bash
set -e

# RemoteRelay Updater Script
# Checks for updates on GitHub and installs them over the current installation.

UPDATE_SCRIPT_VERSION="1.0.0"
echo "RemoteRelay Updater v${UPDATE_SCRIPT_VERSION}"
echo ""

GITHUB_REPO="RebelliousPebble/RemoteRelay"

PRE_RELEASE=false
while [[ "$#" -gt 0 ]]; do
    case $1 in
        -p|--pre-release) PRE_RELEASE=true ;;
        *) echo "Unknown parameter passed: $1"; exit 1 ;;
    esac
    shift
done

INSTALL_DIR_BASE="/home/$SUDO_USER/RemoteRelay" # Approximation, refined below
if [ -z "$SUDO_USER" ]; then
  echo "Error: This script must be run as root (sudo)." >&2
  exit 1
fi
USER_HOME=$(eval echo ~$SUDO_USER)
BASE_INSTALL_DIR="$USER_HOME/RemoteRelay"
SERVER_INSTALL_DIR="$BASE_INSTALL_DIR/server"
CLIENT_INSTALL_DIR="$BASE_INSTALL_DIR/client"
BACKUP_DIR="$USER_HOME/.remoterelay-backups"

# Network connectivity check
check_network() {
    echo "Checking network connectivity..."
    if ! curl -s --connect-timeout 5 "https://api.github.com" >/dev/null 2>&1; then
        echo "Error: Cannot reach GitHub. Check your network connection." >&2
        exit 1
    fi
    echo "  ✓ Network connectivity OK"
}

# Create backup before update
create_backup() {
    local timestamp
    timestamp=$(date +%Y%m%d_%H%M%S)
    local backup_path="$BACKUP_DIR/backup_$timestamp"
    
    echo "Creating backup before update..."
    mkdir -p "$backup_path"
    
    # Backup config files
    if [ -d "$SERVER_INSTALL_DIR" ]; then
        mkdir -p "$backup_path/server"
        cp "$SERVER_INSTALL_DIR/config.json" "$backup_path/server/" 2>/dev/null || true
        cp "$SERVER_INSTALL_DIR/appsettings.json" "$backup_path/server/" 2>/dev/null || true
    fi
    
    if [ -d "$CLIENT_INSTALL_DIR" ]; then
        mkdir -p "$backup_path/client"
        cp "$CLIENT_INSTALL_DIR/ClientConfig.json" "$backup_path/client/" 2>/dev/null || true
    fi
    
    echo "  ✓ Backup created at $backup_path"
    
    # Maintain only last 3 backups
    cleanup_old_backups
}

cleanup_old_backups() {
    local backup_count
    backup_count=$(find "$BACKUP_DIR" -maxdepth 1 -type d -name "backup_*" 2>/dev/null | wc -l)
    
    if [ "$backup_count" -gt 3 ]; then
        echo "  Cleaning up old backups (keeping last 3)..."
        # Use null-terminated strings for safety with special characters in paths
        find "$BACKUP_DIR" -maxdepth 1 -type d -name "backup_*" -print0 | sort -z | head -z -n $((backup_count - 3)) | xargs -0 rm -rf
    fi
}

check_network

echo "Checking for updates..."

# Get latest release info
if ! command -v jq >/dev/null 2>&1 || ! command -v curl >/dev/null 2>&1; then
    echo "Error: curl and jq are required. Please install them."
    exit 1
fi

if [ "$PRE_RELEASE" = true ]; then
    echo "Checking for pre-release updates..."
    LATEST_RELEASE_JSON=$(curl -s "https://api.github.com/repos/$GITHUB_REPO/releases" | jq -r '.[0]')
else
    echo "Checking for stable updates..."
    LATEST_RELEASE_JSON=$(curl -s "https://api.github.com/repos/$GITHUB_REPO/releases/latest")
fi

LATEST_VERSION_TAG=$(echo "$LATEST_RELEASE_JSON" | jq -r .tag_name)
LATEST_VERSION=${LATEST_VERSION_TAG#v} # Remove 'v' prefix if present

if [ "$LATEST_VERSION_TAG" == "null" ]; then
    echo "Error: Failed to fetch latest release from GitHub."
    echo "API Response: $LATEST_RELEASE_JSON"
    exit 1
fi

# Get local version
LOCAL_VERSION="0.0.0"
if [ -f "$SERVER_INSTALL_DIR/RemoteRelay.Server" ]; then
   LOCAL_VERSION=$("$SERVER_INSTALL_DIR/RemoteRelay.Server" --version 2>/dev/null || echo "0.0.0")
elif [ -f "$CLIENT_INSTALL_DIR/RemoteRelay" ]; then
   LOCAL_VERSION=$("$CLIENT_INSTALL_DIR/RemoteRelay" --version 2>/dev/null || echo "0.0.0")
fi

# Simple version comparison
# Function to convert version string to comparable number (e.g., 1.2.3 -> 1002003)
version_to_int() {
  echo "$@" | awk -F. '{ printf("%d%03d%03d\n", $1,$2,$3); }';
}

echo "Current Version: $LOCAL_VERSION"
echo "Latest Version:  $LATEST_VERSION"

if [ $(version_to_int "$LATEST_VERSION") -le $(version_to_int "$LOCAL_VERSION") ]; then
    echo "RemoteRelay is up to date."
    exit 0
fi

echo "Update available!"

# Create backup before proceeding
create_backup

echo "Downloading installer..."

# Find asset URL for linux-arm64
ASSET_URL=$(echo "$LATEST_RELEASE_JSON" | jq -r '.assets[] | select(.name | contains("arm64") and contains(".sh")) | .browser_download_url' | head -n 1)

if [ -z "$ASSET_URL" ] || [ "$ASSET_URL" == "null" ]; then
    echo "Error: Could not find suitable installer asset (arm64 .sh) in release."
    exit 1
fi

TEMP_INSTALLER="/tmp/remoterelay_update_installer.sh"
curl -L -o "$TEMP_INSTALLER" "$ASSET_URL"

# Verify download
if [ ! -f "$TEMP_INSTALLER" ]; then
    echo "Error: Download failed - file not found." >&2
    exit 1
fi

DOWNLOAD_SIZE=$(stat -c%s "$TEMP_INSTALLER" 2>/dev/null || stat -f%z "$TEMP_INSTALLER" 2>/dev/null || echo "0")
if [ "$DOWNLOAD_SIZE" -lt 10000 ]; then
    echo "Error: Downloaded file is too small ($DOWNLOAD_SIZE bytes). Download may be corrupted." >&2
    rm -f "$TEMP_INSTALLER"
    exit 1
fi

echo "  ✓ Download verified ($DOWNLOAD_SIZE bytes)"

chmod +x "$TEMP_INSTALLER"

echo "Running installer..."
# Run the installer. The user will see the prompts.
# The existing install.sh detects existing installation and asks "Update server component? [Y/n]"
# It defaults to Y for updates if installed, so pressing Enter is enough.
"$TEMP_INSTALLER"

# Cleanup
rm -f "$TEMP_INSTALLER"
echo "Update complete."

