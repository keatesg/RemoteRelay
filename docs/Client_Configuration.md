# Client Configuration

The RemoteRelay client is configured using the `ClientConfig.json` file. This file defines the server connection details and controls which inputs and outputs are visible in the user interface.

## In-App Connection Settings

The server connection no longer requires editing JSON by hand. The
**🔗 Connection** button in the client opens a dialog where you can:

- scan the local network for RemoteRelay servers (mDNS) and pick one, or
- enter a host/port manually, or
- leave the host blank to keep automatic discovery.

The dialog also opens automatically on first run when nothing is configured
and no server can be found. Saved choices are written to `ClientConfig.json`,
so everything below still applies if you prefer to edit the file directly.

## Configuration File Location

- **Development**: `RemoteRelay/ServerDetails.json` (legacy) or `ClientConfig.json`
- **Installed (Linux)**: Same directory as the `RemoteRelay` executable, named `ClientConfig.json`
- **Installed (Windows)**: `%ProgramData%\RemoteRelay\Client\ClientConfig.json`

## Configuration File Format

```json
{
  "Host": "localhost",
  "Port": 33101,
  "ShownInputs": [
    "Input 1",
    "Input 2"
  ],
  "ShownOutputs": [
    "Output 1",
    "Output 2"
  ]
}
```

## Configuration Properties

### Host (Optional)

The IP address or hostname of the RemoteRelay server. When empty or omitted
(or set to `localhost`), the client auto-discovers the server on the local
network via mDNS at startup.

```json
"Host": "192.168.1.100"
```

**Examples:**
- `""` - Auto-discover the server on the local network
- `"localhost"` - Connect to server on the same machine (also triggers discovery)
- `"192.168.1.100"` - Connect to server at specific IP address
- `"relay.example.com"` - Connect using hostname/domain
- `"10.0.0.5"` - Connect to server on local network

### Port (Required)

The TCP port the server is listening on. This must match the `ServerPort` setting in the server's configuration.

```json
"Port": 33101
```

- Default: `33101`
- Must match the server's `ServerPort` configuration
- Valid range: 1-65535

### ShownInputs (Optional)

An array of input source names to display in the client UI. Only sources listed here will be visible to the user.

```json
"ShownInputs": [
  "Input 1",
  "Input 2",
  "Input 3"
]
```

**Behavior:**
- If present: Only the specified sources are shown in the UI
- If omitted or `null`: All sources defined on the server are shown
- Source names must exactly match those defined in the server's `Routes` configuration
- Useful for creating multiple clients with different access levels

**Use Cases:**
- **Operator Interface**: Show only the sources that operator needs to access
- **Department-Specific**: Each department sees only their relevant sources

### ShownOutputs (Optional)

An array of output names to display in the client UI. Only outputs listed here will be visible to the user.

```json
"ShownOutputs": [
  "Output 1",
  "Output 2"
]
```

**Behavior:**
- If present: Only the specified outputs are shown in the UI (multi-output mode)
- If omitted or `null`: All outputs defined on the server are shown
- Output names must exactly match those defined in the server's `Routes` configuration
- When only one output is visible, the client operates in single-output mode

**UI Modes:**
- **Single Output Mode**: If only one output is shown (or ShownOutputs has one item), displays simplified UI with large buttons
- **Multi Output Mode**: If multiple outputs are shown, displays each output as a separate section with its own source buttons

### LastDiscoveredHost / LastDiscoveredPort (Managed automatically)

When auto-discovery finds a server, the client caches its address in these
properties. If a later launch can't discover a server (e.g. mDNS is blocked or
temporarily down), the client falls back to this cached address. You never
need to set these by hand, and they are ignored whenever `Host` is set
explicitly.

## Example Configurations

### Simple Client (Single Output)

Connect to a server and show all available sources for a single output:

```json
{
  "Host": "192.168.1.50",
  "Port": 33101
}
```

### Filtered Client (Specific Sources)

Show only specific sources that a particular user should access:

```json
{
  "Host": "192.168.1.50",
  "Port": 33101,
  "ShownInputs": [
    "Studio A",
    "Studio B"
  ]
}
```

This configuration would hide other sources (like "Backup" or "Emergency") from the user.

### Multi-Output Client

Display a specific set of sources and outputs:

```json
{
  "Host": "192.168.1.50",
  "Port": 33101,
  "ShownInputs": [
    "Mic 1",
    "Mic 2",
    "Playback"
  ],
  "ShownOutputs": [
    "Stage PA",
    "Monitor Mix",
    "Recording"
  ]
}
```

### Single Output Mode (Explicit)

Force single-output mode by showing only one output:

```json
{
  "Host": "192.168.1.50",
  "Port": 33101,
  "ShownInputs": [
    "Studio A",
    "Studio B",
    "Automation"
  ],
  "ShownOutputs": [
    "Transmitter"
  ]
}
```

This displays a simplified UI optimized for switching a single output between multiple sources.

## Legacy Configuration Format

Older versions used a simpler `ServerDetails.json` format that only specified connection details:

```json
{
  "Host": "localhost",
  "Port": 33101
}
```

This format is still supported but doesn't allow filtering of inputs/outputs. The client will automatically detect which format is being used.

## Connection Behavior

- The client will attempt to connect to the server on startup
- If the connection fails, it will show a connection error and retry automatically
- When the connection is established, the client downloads the current relay state
- Changes made on any client (or via physical buttons) are synchronized to all connected clients in real-time
- If the server goes offline, clients will show a disconnected state and attempt to reconnect

## Troubleshooting

### Cannot Connect to Server

**Check the following:**
1. Verify the `Host` IP address is correct
2. Ensure the `Port` matches the server's `ServerPort` setting
3. Check that the server is running
4. Verify firewall rules allow connections on the specified port
5. Test connectivity with `ping` or `telnet` to the server

### Sources Not Appearing

**Possible causes:**
1. `ShownInputs` array doesn't include the source names exactly as defined on the server
2. Source names are case-sensitive - "Input 1" ≠ "input 1"
3. Server's `Routes` configuration doesn't include those sources

### UI Shows Wrong Mode

**Mode Selection:**
- **Single Output Mode**: Appears when only one output is visible (either by having one output defined on server, or `ShownOutputs` containing one item)
- **Multi Output Mode**: Appears when multiple outputs are visible

Check your `ShownOutputs` configuration to control which mode is used.

## Multiple Client Configurations

You can create multiple configuration files for different users or purposes:

**Operator Client** (`ClientConfig.json`):
```json
{
  "Host": "192.168.1.50",
  "Port": 33101,
  "ShownInputs": ["Studio A", "Studio B", "Automation"]
}
```

**Engineering Client** (`ClientConfig-Engineering.json`):
```json
{
  "Host": "192.168.1.50",
  "Port": 33101
}
```
(Shows all sources and outputs for full control)

To use alternate configurations, rename or copy the desired config file to `ClientConfig.json` before launching the client.
