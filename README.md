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

### Recommended: Automated Installer (Raspberry Pi)

The easiest way to install RemoteRelay on a Raspberry Pi:

1. **Download the installer:**
   ```bash
   wget https://github.com/yourusername/RemoteRelay/releases/latest/download/remote-relay-installer.sh
   ```

2. **Make it executable:**
   ```bash
   chmod +x remote-relay-installer.sh
   ```

3. **Run the installer:**
   ```bash
   sudo ./remote-relay-installer.sh
   ```

The installer will guide you through setting up the Server, Client, or both, and configure automatic startup services.


## Configuration

Configuration is done through JSON files:

- **Server**: Edit `config.json` to define relay pins, routing, and options
- **Client**: Edit `ClientConfig.json` to specify server connection and UI filtering

## Documentation

Detailed configuration guides and feature documentation:

- **[Server Configuration](docs/Server_Configuration.md)** - GPIO pins, routing tables, physical buttons, and server options
- **[Client Configuration](docs/Client_Configuration.md)** - Server connection and UI filtering
- **[Inactive Relay Feature](docs/Inactive_Relay.md)** - Fail-safe relay for backup routing when the system is offline

## License

See LICENSE file for details.
