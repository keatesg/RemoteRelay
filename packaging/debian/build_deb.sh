#!/bin/bash
set -euo pipefail

# build_deb.sh — Builds native Debian packages (.deb) for RemoteRelay
#
# Usage:
#   ./packaging/debian/build_deb.sh [TARGET_RUNTIME] [VERSION] [OUTPUT_DIR]
#
# Examples:
#   ./packaging/debian/build_deb.sh linux-arm64 1.0.0 ./dist
#   ./packaging/debian/build_deb.sh linux-x64 1.0.0 ./dist

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd "$SCRIPT_DIR/../.." && pwd)

TARGET_RUNTIME="${1:-linux-arm64}"
VERSION="${2:-${Version:-${VERSION:-1.0.0}}}"
OUTPUT_DIR="${3:-$ROOT_DIR/dist}"

# Clean version string (remove leading 'v' if present)
VERSION="${VERSION#v}"
VERSION="${VERSION%%-*}"

# Map .NET Runtime Identifier to Debian Architecture
case "$TARGET_RUNTIME" in
  linux-arm64)
    DEB_ARCH="arm64"
    ;;
  linux-arm)
    DEB_ARCH="armhf"
    ;;
  linux-x64)
    DEB_ARCH="amd64"
    ;;
  *)
    echo "Unsupported target runtime: $TARGET_RUNTIME" >&2
    exit 1
    ;;
esac

echo "=========================================="
echo "Building RemoteRelay Debian Packages (.deb)"
echo "Target Runtime: $TARGET_RUNTIME"
echo "Debian Arch:    $DEB_ARCH"
echo "Version:        $VERSION"
echo "Output Dir:     $OUTPUT_DIR"
echo "=========================================="

if ! command -v dpkg-deb >/dev/null 2>&1; then
  echo "Error: dpkg-deb not found. Please install dpkg (e.g. sudo apt-get install dpkg)." >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"

STAGE_ROOT="$ROOT_DIR/obj/deb_stage/$TARGET_RUNTIME"
SERVER_STAGE="$STAGE_ROOT/remoterelay-server"
CLIENT_STAGE="$STAGE_ROOT/remoterelay-client"

rm -rf "$STAGE_ROOT"
mkdir -p "$SERVER_STAGE" "$CLIENT_STAGE"

echo "--> Publishing RemoteRelay.Server..."
dotnet publish "$ROOT_DIR/RemoteRelay.Server/RemoteRelay.Server.csproj" \
  --configuration Release \
  --runtime "$TARGET_RUNTIME" \
  -p:PublishProfile=LinuxSelfContained \
  -p:Version="$VERSION" \
  -o "$SERVER_STAGE/usr/lib/remoterelay/server"

echo "--> Publishing RemoteRelay.Configurator..."
dotnet publish "$ROOT_DIR/RemoteRelay.Configurator/RemoteRelay.Configurator.csproj" \
  --configuration Release \
  --runtime "$TARGET_RUNTIME" \
  -p:PublishProfile=LinuxSelfContained \
  -p:Version="$VERSION" \
  -o "$SERVER_STAGE/usr/lib/remoterelay/configurator"

echo "--> Publishing RemoteRelay Client..."
dotnet publish "$ROOT_DIR/RemoteRelay/RemoteRelay.csproj" \
  --configuration Release \
  --runtime "$TARGET_RUNTIME" \
  -p:PublishProfile=LinuxSelfContained \
  -p:Version="$VERSION" \
  -o "$CLIENT_STAGE/usr/lib/remoterelay/client"

# -----------------------------------------------------------------------------
# Assemble remoterelay-server package
# -----------------------------------------------------------------------------
echo "--> Assembling remoterelay-server package..."

mkdir -p "$SERVER_STAGE/usr/bin"
ln -sf /usr/lib/remoterelay/server/RemoteRelay.Server "$SERVER_STAGE/usr/bin/remoterelay-server"
ln -sf /usr/lib/remoterelay/configurator/RemoteRelay.Configurator "$SERVER_STAGE/usr/bin/remoterelay-config"

mkdir -p "$SERVER_STAGE/lib/systemd/system"
cp "$SCRIPT_DIR/server/systemd/remote-relay-server.service" "$SERVER_STAGE/lib/systemd/system/"

mkdir -p "$SERVER_STAGE/etc/remoterelay"
cp "$SCRIPT_DIR/server/config/config.json" "$SERVER_STAGE/etc/remoterelay/"

mkdir -p "$SERVER_STAGE/usr/share/applications"
cp "$SCRIPT_DIR/server/desktop/remoterelay-config.desktop" "$SERVER_STAGE/usr/share/applications/"

mkdir -p "$SERVER_STAGE/DEBIAN"
cp "$SCRIPT_DIR/server/DEBIAN/conffiles" "$SERVER_STAGE/DEBIAN/"
cp "$SCRIPT_DIR/server/DEBIAN/preinst" "$SERVER_STAGE/DEBIAN/"
cp "$SCRIPT_DIR/server/DEBIAN/postinst" "$SERVER_STAGE/DEBIAN/"
cp "$SCRIPT_DIR/server/DEBIAN/prerm" "$SERVER_STAGE/DEBIAN/"
cp "$SCRIPT_DIR/server/DEBIAN/postrm" "$SERVER_STAGE/DEBIAN/"

chmod 755 "$SERVER_STAGE/DEBIAN/preinst" "$SERVER_STAGE/DEBIAN/postinst" "$SERVER_STAGE/DEBIAN/prerm" "$SERVER_STAGE/DEBIAN/postrm"
chmod 644 "$SERVER_STAGE/DEBIAN/conffiles"
chmod 644 "$SERVER_STAGE/lib/systemd/system/remote-relay-server.service"
chmod 644 "$SERVER_STAGE/etc/remoterelay/config.json"
chmod 644 "$SERVER_STAGE/usr/share/applications/remoterelay-config.desktop"

SERVER_SIZE=$(du -sk "$SERVER_STAGE" | cut -f1)

cat > "$SERVER_STAGE/DEBIAN/control" <<EOF
Package: remoterelay-server
Version: ${VERSION}
Section: net
Priority: optional
Architecture: ${DEB_ARCH}
Installed-Size: ${SERVER_SIZE}
Maintainer: RemoteRelay Developers <https://github.com/keatesg/RemoteRelay>
Depends: libc6, libgcc-s1, libstdc++6
Recommends: libx11-6, libice6, libsm6, libfontconfig1
Description: RemoteRelay Server and Configurator
 Server daemon and graphical configurator for RemoteRelay hardware switching systems.
 RemoteRelay Server manages relay hardware drivers, routing logic, SignalR API, and
 UDP endpoints. This package also includes the RemoteRelay Configurator desktop GUI
 tool for configuring hardware, routes, and security PINs.
EOF
chmod 644 "$SERVER_STAGE/DEBIAN/control"

# -----------------------------------------------------------------------------
# Assemble remoterelay-client package
# -----------------------------------------------------------------------------
echo "--> Assembling remoterelay-client package..."

mkdir -p "$CLIENT_STAGE/usr/bin"
ln -sf /usr/lib/remoterelay/client/RemoteRelay "$CLIENT_STAGE/usr/bin/remoterelay-client"

mkdir -p "$CLIENT_STAGE/usr/share/applications"
cp "$SCRIPT_DIR/client/desktop/remoterelay.desktop" "$CLIENT_STAGE/usr/share/applications/"

mkdir -p "$CLIENT_STAGE/DEBIAN"
cp "$SCRIPT_DIR/client/DEBIAN/postinst" "$CLIENT_STAGE/DEBIAN/"
cp "$SCRIPT_DIR/client/DEBIAN/postrm" "$CLIENT_STAGE/DEBIAN/"

chmod 755 "$CLIENT_STAGE/DEBIAN/postinst" "$CLIENT_STAGE/DEBIAN/postrm"
chmod 644 "$CLIENT_STAGE/usr/share/applications/remoterelay.desktop"

CLIENT_SIZE=$(du -sk "$CLIENT_STAGE" | cut -f1)

cat > "$CLIENT_STAGE/DEBIAN/control" <<EOF
Package: remoterelay-client
Version: ${VERSION}
Section: x11
Priority: optional
Architecture: ${DEB_ARCH}
Installed-Size: ${CLIENT_SIZE}
Maintainer: RemoteRelay Developers <https://github.com/keatesg/RemoteRelay>
Depends: libc6, libgcc-s1, libstdc++6, libx11-6, libfontconfig1
Recommends: libice6, libsm6, libxcursor1, libxext6, libxi6, libxrandr2, libxrender1
Description: RemoteRelay Touch Client
 Touch-screen client interface for RemoteRelay hardware switching systems.
 Connects to a RemoteRelay Server over SignalR, displays real-time input/output
 status, and supports per-client display filtering.
EOF
chmod 644 "$CLIENT_STAGE/DEBIAN/control"

# -----------------------------------------------------------------------------
# Build .deb packages
# -----------------------------------------------------------------------------
SERVER_DEB="$OUTPUT_DIR/remoterelay-server_${VERSION}_${DEB_ARCH}.deb"
CLIENT_DEB="$OUTPUT_DIR/remoterelay-client_${VERSION}_${DEB_ARCH}.deb"

echo "--> Building $SERVER_DEB..."
dpkg-deb --build --root-owner-group "$SERVER_STAGE" "$SERVER_DEB"

echo "--> Building $CLIENT_DEB..."
dpkg-deb --build --root-owner-group "$CLIENT_STAGE" "$CLIENT_DEB"

echo ""
echo "=========================================="
echo "Debian packages successfully created:"
echo "  $SERVER_DEB"
echo "  $CLIENT_DEB"
echo "=========================================="
