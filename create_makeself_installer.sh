#!/bin/bash

# Script to create a makeself installer for RemoteRelay (Linux ARM64)
# This script is intended to be run in a Linux environment (e.g., WSL)
# where .NET SDK and makeself are installed.

# Exit immediately if a command exits with a non-zero status.
set -e

# --- Configuration ---
SOLUTION_FILE="RemoteRelay.sln"
CLIENT_PROJECT_FILE="RemoteRelay/RemoteRelay.csproj"
SERVER_PROJECT_FILE="RemoteRelay.Server/RemoteRelay.Server.csproj"
CONFIGURATION="Release"

# Default to linux-arm64 if no argument provided
if [ -z "$1" ]; then
  TARGET_RUNTIME="linux-arm64"
else
  TARGET_RUNTIME="$1"
fi

PUBLISH_DIR_BASE="./publish" # Relative to script location (project root)
CLIENT_PUBLISH_DIR="${PUBLISH_DIR_BASE}/RemoteRelay-Client-${TARGET_RUNTIME}"
SERVER_PUBLISH_DIR="${PUBLISH_DIR_BASE}/RemoteRelay-Server-${TARGET_RUNTIME}"

MAKSELF_STAGE_DIR="./makeself_stage" # Relative to script location
CLIENT_STAGE_FILES_DIR="${MAKSELF_STAGE_DIR}/client_files"
SERVER_STAGE_FILES_DIR="${MAKSELF_STAGE_DIR}/server_files"

INSTALLER_NAME="remote-relay-installer-${TARGET_RUNTIME}.sh"
INSTALLER_LABEL="RemoteRelay Installer (${TARGET_RUNTIME})"
# This is the script within your project that the makeself archive will execute upon extraction.
# It should be present in the root of your project.
STARTUP_SCRIPT_SOURCE="install.sh"
STARTUP_SCRIPT_DEST_IN_ARCHIVE="install.sh"
UNINSTALL_SCRIPT_SOURCE="uninstall.sh"
UNINSTALL_SCRIPT_DEST_IN_ARCHIVE="uninstall.sh"
UPDATE_SCRIPT_SOURCE="update.sh"


# --- Helper Functions ---
check_command() {
  if ! command -v "$1" &> /dev/null; then
    echo "Error: Command '$1' not found. Please ensure it is installed and in your PATH."
    if [ "$1" == "makeself" ]; then
      echo "On Debian/Ubuntu, you can typically install it using: sudo apt-get update && sudo apt-get install -y makeself"
    elif [ "$1" == "dotnet" ]; then
      echo "Please install the .NET SDK from https://dotnet.microsoft.com/download"
    fi
    exit 1
  fi
}

# --- Main Script ---

echo "----------------------------------------------------"
echo "RemoteRelay Makeself Installer Creation Script"
echo "----------------------------------------------------"
echo "Target Runtime: ${TARGET_RUNTIME}"
echo ""

echo "Step 1: Checking prerequisites..."
check_command "dotnet"
check_command "makeself"
# dos2unix is helpful but not strictly required - we have a fallback with sed
if ! command -v "dos2unix" &> /dev/null; then
    echo "Note: dos2unix not found. Will use sed fallback for line ending conversion."
fi
echo "Prerequisites met."
echo ""

echo "Step 2: Cleaning up previous artifacts..."
# Only clean specifically for the target runtime to avoid wiping other parallel builds if we were doing that,
# but for simplicity, we wipe the stage dir which is shared.
# PUBLISH_DIR_BASE contains specific subdirs, so we can leave it or clean it.
# Let's clean the specific publish dirs to be safe.
rm -rf "${CLIENT_PUBLISH_DIR}"
rm -rf "${SERVER_PUBLISH_DIR}"
rm -rf "${MAKSELF_STAGE_DIR}"
rm -f "${INSTALLER_NAME}"
echo "Cleanup complete."
echo ""

echo "Step 3: Restoring .NET dependencies..."
dotnet restore "${SOLUTION_FILE}"
echo "Dependency restoration complete."
echo ""

echo "Step 4: Building solution (${CONFIGURATION} mode)..."
dotnet build "${SOLUTION_FILE}" --configuration "${CONFIGURATION}" --no-restore
echo "Build complete."
echo ""

echo "Step 5: Publishing RemoteRelay Client for ${TARGET_RUNTIME}..."
# The GitHub Action uses /p:PublishProfile=FolderProfile.
# This script assumes 'FolderProfile' is configured for linux-arm64 or that the project targets it by default for this profile.
# If not, you might need to add --runtime ${TARGET_RUNTIME} --self-contained true to the publish command,
# or ensure 'RemoteRelay/Properties/PublishProfiles/FolderProfile.pubxml' specifies <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>.
# We explicitely add --runtime here to override/ensure it matches the requested target.
dotnet publish "${CLIENT_PROJECT_FILE}" --configuration "${CONFIGURATION}" --runtime "${TARGET_RUNTIME}" /p:PublishProfile=LinuxSelfContained -o "${CLIENT_PUBLISH_DIR}"
echo "Client publish complete. Output: ${CLIENT_PUBLISH_DIR}"
echo ""

echo "Step 6: Publishing RemoteRelay Server for ${TARGET_RUNTIME}..."
# Similar assumption for 'RemoteRelay.Server/Properties/PublishProfiles/FolderProfile.pubxml'
dotnet publish "${SERVER_PROJECT_FILE}" --configuration "${CONFIGURATION}" --runtime "${TARGET_RUNTIME}" /p:PublishProfile=LinuxSelfContained -o "${SERVER_PUBLISH_DIR}"
echo "Server publish complete. Output: ${SERVER_PUBLISH_DIR}"
echo ""

echo "Step 7: Staging files for makeself..."
mkdir -p "${CLIENT_STAGE_FILES_DIR}"
mkdir -p "${SERVER_STAGE_FILES_DIR}"

echo "  Copying all client files from ${CLIENT_PUBLISH_DIR}..."
# Ensure target directory exists and is clean for a full copy
rm -rf "${CLIENT_STAGE_FILES_DIR}"
mkdir -p "${CLIENT_STAGE_FILES_DIR}"
cp -a "${CLIENT_PUBLISH_DIR}/." "${CLIENT_STAGE_FILES_DIR}/"

echo "  Copying all server files from ${SERVER_PUBLISH_DIR}..."
# Ensure target directory exists and is clean for a full copy
rm -rf "${SERVER_STAGE_FILES_DIR}"
mkdir -p "${SERVER_STAGE_FILES_DIR}"
cp -a "${SERVER_PUBLISH_DIR}/." "${SERVER_STAGE_FILES_DIR}/"

echo "  Copying and preparing startup script (${STARTUP_SCRIPT_SOURCE})..."
if [ ! -f "${STARTUP_SCRIPT_SOURCE}" ]; then
    echo "Error: Startup script '${STARTUP_SCRIPT_SOURCE}' not found in project root. This script is needed for the installer."
    exit 1
fi
cp "${STARTUP_SCRIPT_SOURCE}" "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}"
# Add this line to fix potential CRLF issues if install.sh comes from Windows
# Using sed for more reliable line ending conversion and ensuring Unix format
sed -i 's/\r$//' "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}"
# Ensure the script has Unix line endings and is executable
dos2unix "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}" 2>/dev/null || true
chmod +x "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}"

echo "  Copying and preparing uninstall script (${UNINSTALL_SCRIPT_SOURCE})..."
if [ ! -f "${UNINSTALL_SCRIPT_SOURCE}" ]; then
    echo "Warning: Uninstall script '${UNINSTALL_SCRIPT_SOURCE}' not found in project root. Uninstall functionality will not be available."
else
    cp "${UNINSTALL_SCRIPT_SOURCE}" "${MAKSELF_STAGE_DIR}/${UNINSTALL_SCRIPT_DEST_IN_ARCHIVE}"
    # Fix potential CRLF issues
    sed -i 's/\r$//' "${MAKSELF_STAGE_DIR}/${UNINSTALL_SCRIPT_DEST_IN_ARCHIVE}"
    dos2unix "${MAKSELF_STAGE_DIR}/${UNINSTALL_SCRIPT_DEST_IN_ARCHIVE}" 2>/dev/null || true
    chmod +x "${MAKSELF_STAGE_DIR}/${UNINSTALL_SCRIPT_DEST_IN_ARCHIVE}"
fi

echo "  Copying and preparing update script (${UPDATE_SCRIPT_SOURCE})..."
if [ -f "${UPDATE_SCRIPT_SOURCE}" ]; then
    cp "${UPDATE_SCRIPT_SOURCE}" "${MAKSELF_STAGE_DIR}/update.sh"
    sed -i 's/\r$//' "${MAKSELF_STAGE_DIR}/update.sh"
    dos2unix "${MAKSELF_STAGE_DIR}/update.sh" 2>/dev/null || true
    chmod +x "${MAKSELF_STAGE_DIR}/update.sh"
else
    echo "Warning: Update script '${UPDATE_SCRIPT_SOURCE}' not found."
fi

echo "  Copying shared UI libraries (lib/)..."
mkdir -p "${MAKSELF_STAGE_DIR}/lib"
for libf in rr-ui.sh rr-common.sh; do
    if [ -f "lib/${libf}" ]; then
        cp "lib/${libf}" "${MAKSELF_STAGE_DIR}/lib/${libf}"
        sed -i 's/\r$//' "${MAKSELF_STAGE_DIR}/lib/${libf}"
        dos2unix "${MAKSELF_STAGE_DIR}/lib/${libf}" 2>/dev/null || true
    else
        echo "Warning: lib/${libf} not found — management tool will not work."
    fi
done

echo "  Copying management tool (remoterelay.sh)..."
if [ -f "remoterelay.sh" ]; then
    cp "remoterelay.sh" "${MAKSELF_STAGE_DIR}/remoterelay.sh"
    sed -i 's/\r$//' "${MAKSELF_STAGE_DIR}/remoterelay.sh"
    dos2unix "${MAKSELF_STAGE_DIR}/remoterelay.sh" 2>/dev/null || true
    chmod +x "${MAKSELF_STAGE_DIR}/remoterelay.sh"
else
    echo "Warning: remoterelay.sh not found — management tool will not be installed."
fi

# Verify the install script is properly formatted
echo "  Verifying install script..."
if ! head -1 "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}" | grep -q "#!/bin/bash"; then
    echo "Warning: Install script may not have proper shebang line"
fi
if [ ! -x "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}" ]; then
    echo "Error: Install script is not executable after chmod"
    exit 1
fi
echo "Staging complete. Staging directory: ${MAKSELF_STAGE_DIR}"
echo ""

echo "Step 8: Running makeself to create the installer..."
# Verify the staging directory structure before creating the archive
echo "  Staging directory contents:"
ls -la "${MAKSELF_STAGE_DIR}"
echo "  Install script details:"
ls -la "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}"
file "${MAKSELF_STAGE_DIR}/${STARTUP_SCRIPT_DEST_IN_ARCHIVE}" || true

# Using --gzip for compression, which is common.
# The startup script path for makeself is relative to the root of the archive.
# Make sure the startup script is properly referenced with ./ prefix
makeself --gzip "${MAKSELF_STAGE_DIR}" "${INSTALLER_NAME}" "${INSTALLER_LABEL}" "./${STARTUP_SCRIPT_DEST_IN_ARCHIVE}"
echo "Makeself execution complete."
echo ""

echo "----------------------------------------------------"
echo "Installer Creation Successful!"
echo "----------------------------------------------------"
echo "Installer file: ${INSTALLER_NAME}"
echo "Label:          ${INSTALLER_LABEL}"
echo ""
echo "To use the installer:"
echo "1. Transfer ${INSTALLER_NAME} to a ${TARGET_RUNTIME} machine."
echo "2. Make it executable: chmod +x ./${INSTALLER_NAME}"
echo "3. Run it with sudo: sudo ./${INSTALLER_NAME}"
echo ""
echo "Note: This script and the generated installer are for ${TARGET_RUNTIME}."
echo "Ensure you run this script in a Linux-compatible environment (like WSL) with .NET SDK and makeself installed."
echo ""

# --- End of Script ---