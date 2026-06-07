# RemoteRelay

RemoteRelay is a remotely controlled relay switcher designed for professional audio routing applications, primarily in radio broadcasting and studio environments.

## What It Does

RemoteRelay provides seamless remote control of physical relays through an intuitive touch-friendly interface. The system consists of:

- **Server** - Runs on a Raspberry Pi with a relay HAT, controlling GPIO pins and managing connections
- **Client** - Cross-platform UI application that connects to the server for monitoring and control

Multiple clients can connect simultaneously, with all state changes synchronized in real-time. The system supports both simple single-output switching (e.g., selecting which studio feeds a transmitter) and complex multi-output routing (e.g., routing multiple sources to multiple destinations).

![RemoteRelay UI](img/ui.png)

## Key Features

- Real-time synchronization across all connected clients
- Support for physical hardware buttons wired directly to the server
- Single-output and multi-output operating modes
- Optional inactive relay for fail-safe backup routing
- Touch-optimized interface suitable for studio control panels
- Customizable UI filtering per client
- Per-source custom colours

## Installation

### Recommended: One-line install (Raspberry Pi / Linux)

Open a terminal on the Pi and run:

```bash
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash
```

That's it. The bootstrap detects your architecture, downloads the matching
build, and launches a friendly installer that walks you through setting up the
Server, Client, or both — including auto-start services and (optionally) kiosk
display settings.

Variations:

```bash
# Install the latest pre-release
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash -s -- --pre-release

# Fully unattended (no prompts; sensible defaults)
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash -s -- --unattended

# Only one component
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash -s -- --server-only
```

### Managing an installation

After installing, manage everything from one place:

```bash
sudo remoterelay
```

This opens a menu to check status, start/stop the server, update, view logs,
uninstall, and configure the options that aren't set from the client app
(relay driver, K8090 serial port, default source, client connection, kiosk
display, NTP). The same actions are available non-interactively, e.g.
`sudo remoterelay status`, `sudo remoterelay restart`, `sudo remoterelay update`.

See **[Linux Installer & Management](docs/Linux_Installer.md)** for details.

### Manual install / offline

Each release also attaches self-extracting installers
(`RemoteRelay-Installer-linux-arm64.sh`, `RemoteRelay-Installer-linux-arm.sh`,
`RemoteRelay-Installer-linux-x64.sh`) for offline use:

```bash
chmod +x RemoteRelay-Installer-linux-arm64.sh
sudo ./RemoteRelay-Installer-linux-arm64.sh
```

### Windows

A Windows MSI installer (`RemoteRelay-Setup.msi`) is attached to each GitHub Release. It lets you install the Client, Server, or both via a feature-selection wizard, captures the basic configuration each component needs, and registers the Server as an auto-starting Windows Service. See [Windows Installer](docs/Windows_Installer.md) for details.


## Configuration

Configuration is done through JSON files:

- **Server**: Edit `config.json` to define relay pins, routing, and options
- **Client**: Edit `ClientConfig.json` to specify server connection and UI filtering

## Documentation

Detailed configuration guides and feature documentation:

- **[Server Configuration](docs/Server_Configuration.md)** - GPIO pins, routing tables, physical buttons, and server options
- **[Client Configuration](docs/Client_Configuration.md)** - Server connection and UI filtering
- **[Inactive Relay Feature](docs/Inactive_Relay.md)** - Fail-safe relay for backup routing when the system is offline
- **[Linux Installer & Management](docs/Linux_Installer.md)** - One-line install, the `remoterelay` tool, and unattended options
- **[Windows Installer](docs/Windows_Installer.md)** - WiX-based MSI for Windows installs

## License

See LICENSE file for details.
