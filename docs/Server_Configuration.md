# Server Configuration

The server is configured using the `config.json` file located in the same directory as the server executable. This file defines the relay routing, GPIO pin mappings, physical button inputs, and various server options.

## Configuration File Location

- **Development**: `RemoteRelay.Server/config.json`
- **Installed**: Same directory as the `RemoteRelay.Server` executable

## Complete Configuration Example

```json
{
  "Routes": [
    {
      "SourceName": "Input 1",
      "OutputName": "Output 1",
      "RelayPin": 5,
      "ActiveLow": true
    },
    {
      "SourceName": "Input 2",
      "OutputName": "Output 1",
      "RelayPin": 7,
      "ActiveLow": true
    }
  ],
  "DefaultSource": null,
  "DefaultRoutes": {
    "Input 1": "Output 1",
    "Input 2": "Output 2"
  },
  "PhysicalSourceButtons": {
    "Input 1": {
      "PinNumber": 17,
      "TriggerState": "Low"
    }
  },
  "SourceColorPalette": {
    "Input 1": "#FFB85C38",
    "Input 2": "#FF2E8B57"
  },
  "ServerPort": 33101,
  "TcpMirrorAddress": null,
  "TcpMirrorPort": null,
  "InactiveRelay": {
    "Pin": 25,
    "InactiveState": "High"
  },
  "FlashOnSelect": true,
  "ShowIpOnScreen": true,
  "Logging": true,
  "LogoFile": null
}
```

## Configuration Properties

### Routes (Required)

An array defining all possible source-to-output connections and their corresponding GPIO relay pins.

```json
"Routes": [
  {
    "SourceName": "Input 1",
    "OutputName": "Output 1",
    "RelayPin": 5,
    "ActiveLow": true
  }
]
```

**Properties:**
- `SourceName` (string): The name of the input source (e.g., "Input 1", "Studio A", "CD Player")
- `OutputName` (string): The name of the output destination (e.g., "Output 1", "Transmitter", "Monitor")
- `RelayPin` (integer): The GPIO pin number (BCM numbering) that controls this relay
- `ActiveLow` (boolean): 
  - `true`: Relay activates when pin is LOW (common for most relay HATs)
  - `false`: Relay activates when pin is HIGH

**Example Scenarios:**

*Single Output System* (e.g., radio transmitter):
```json
"Routes": [
  { "SourceName": "Studio A", "OutputName": "Transmitter", "RelayPin": 5, "ActiveLow": true },
  { "SourceName": "Studio B", "OutputName": "Transmitter", "RelayPin": 6, "ActiveLow": true },
  { "SourceName": "Backup", "OutputName": "Transmitter", "RelayPin": 7, "ActiveLow": true }
]
```

*Multi-Output System* (e.g., multiple destinations):
```json
"Routes": [
  { "SourceName": "Mic 1", "OutputName": "PA System", "RelayPin": 5, "ActiveLow": true },
  { "SourceName": "Mic 1", "OutputName": "Recording", "RelayPin": 6, "ActiveLow": true },
  { "SourceName": "Mic 2", "OutputName": "PA System", "RelayPin": 7, "ActiveLow": true },
  { "SourceName": "Mic 2", "OutputName": "Recording", "RelayPin": 8, "ActiveLow": true }
]
```

### DefaultRoutes (Optional)

Specifies which output each source should default to when selected. Useful for multi-output systems where each source has a preferred destination.

```json
"DefaultRoutes": {
  "Input 1": "Output 1",
  "Input 2": "Output 2"
}
```

- Keys are source names
- Values are output names
- When a source is selected without specifying an output, it routes to the defined default
- Physical button presses also prefer the matching default route when one is configured
- Ignored if `DefaultSource` is set

### PhysicalSourceButtons (Optional)

Configures physical hardware buttons connected to GPIO pins that can trigger source selections.

```json
"PhysicalSourceButtons": {
  "Input 1": {
    "PinNumber": 17,
    "TriggerState": "Low"
  },
  "Input 2": {
    "PinNumber": 18,
    "TriggerState": "High"
  }
}
```

**Properties:**
- Key: The source name to activate
- `PinNumber` (integer): GPIO pin number (BCM numbering) for the button
- `TriggerState` (string): When the button is considered pressed
  - `"Low"`: Button press pulls pin LOW (common with pull-up resistors)
  - `"High"`: Button press pulls pin HIGH (common with pull-down resistors)

The setup screen includes a `Test Switch` button for each source. That button simulates the saved switch path for the source and is useful for verifying routing logic and client updates. It does not validate the electrical GPIO input itself, so wiring and trigger polarity still need to be checked on hardware.

### SourceColorPalette (Optional)

Overrides the automatically generated source colours used by the client UI.

```json
"SourceColorPalette": {
  "Input 1": "#FFB85C38",
  "Input 2": "#FF2E8B57"
}
```

- Keys are source names
- Values are any Avalonia-compatible colour string, such as `#RRGGBB`, `#AARRGGBB`, or a named colour
- These colours are now used consistently for active and linked source states in both single-output and multi-output client views

### ServerPort (Required)

The TCP port the server listens on for client connections.

```json
"ServerPort": 33101
```

- Default: `33101`
- Clients must connect to this port
- Ensure firewall rules allow connections on this port

### TcpMirrorAddress (Optional)

IP address of a remote system to mirror relay state changes to via TCP.

```json
"TcpMirrorAddress": "192.168.1.100"
```

- Set to an IP address (string) to enable TCP mirroring
- Set to `null` to disable
- Requires `TcpMirrorPort` to be set

### TcpMirrorPort (Optional)

TCP port to send mirror commands to.

```json
"TcpMirrorPort": 8080
```

- Required if `TcpMirrorAddress` is set
- Set to `null` to disable mirroring

### InactiveRelay (Optional)

Configures a "safety" relay that activates when no clients are connected or when the server is shutting down. See [Inactive Relay Feature](Inactive_Relay.md) for more details.

```json
"InactiveRelay": {
  "Pin": 25,
  "InactiveState": "High"
}
```

**Properties:**
- `Pin` (integer): GPIO pin number for the inactive relay
- `InactiveState` (string): The state to set when inactive
  - `"High"`: Pin goes HIGH when system is inactive
  - `"Low"`: Pin goes LOW when system is inactive

Set to `null` to disable this feature.

### FlashOnSelect (Optional)

Controls whether LEDs flash briefly when a source is selected.

```json
"FlashOnSelect": true
```

- `true`: Relays flash on/off briefly when switching (visual feedback)
- `false`: No flashing
- Default: `true`

### ShowIpOnScreen (Optional)

Whether to display the server's IP address on an attached display (if available).

```json
"ShowIpOnScreen": true
```

- `true`: Show IP address on startup
- `false`: Don't show IP
- Default: `false`

### Logging (Optional)

Enable or disable detailed logging to console and files.

```json
"Logging": true
```

- `true`: Enable detailed logging
- `false`: Minimal logging
- Default: `true`

### LogoFile (Optional)

Path to an image file to display on an attached screen.

```json
"LogoFile": "/home/pi/logo.png"
```

- Set to a full file path (string) to display a custom logo
- Set to `null` to disable
- Supports common image formats (PNG, JPG)

## Validation

The server validates the configuration on startup and will report errors if:
- Required properties are missing
- GPIO pin numbers are invalid or duplicated
- Source/output names are referenced inconsistently
- TcpMirror settings are incomplete

Check the server console output for validation messages.

## Example Configurations

### Simple Single-Output Studio Switcher

```json
{
  "Routes": [
    { "SourceName": "Studio A", "OutputName": "Transmitter", "RelayPin": 5, "ActiveLow": true },
    { "SourceName": "Studio B", "OutputName": "Transmitter", "RelayPin": 6, "ActiveLow": true },
    { "SourceName": "Automation", "OutputName": "Transmitter", "RelayPin": 7, "ActiveLow": true }
  ],
  "DefaultSource": "Automation",
  "ServerPort": 33101,
  "InactiveRelay": {
    "Pin": 25,
    "InactiveState": "High"
  },
  "FlashOnSelect": true,
  "Logging": true
}
```

### Multi-Output System with Physical Buttons

```json
{
  "Routes": [
    { "SourceName": "Mic 1", "OutputName": "Stage", "RelayPin": 5, "ActiveLow": true },
    { "SourceName": "Mic 1", "OutputName": "Recording", "RelayPin": 6, "ActiveLow": true },
    { "SourceName": "Mic 2", "OutputName": "Stage", "RelayPin": 7, "ActiveLow": true },
    { "SourceName": "Mic 2", "OutputName": "Recording", "RelayPin": 8, "ActiveLow": true }
  ],
  "DefaultRoutes": {
    "Mic 1": "Stage",
    "Mic 2": "Recording"
  },
  "PhysicalSourceButtons": {
    "Mic 1": { "PinNumber": 17, "TriggerState": "Low" },
    "Mic 2": { "PinNumber": 18, "TriggerState": "Low" }
  },
  "ServerPort": 33101,
  "FlashOnSelect": true,
  "Logging": true
}
```
