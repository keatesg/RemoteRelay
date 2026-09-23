# Server Configuration

The server is configured using the `config.json` file located in the same directory as the server executable. This file defines the relay routing, the relay driver, GPIO pin mappings, physical button inputs, and various server options.

## Configuration File Location

- **Development**: `RemoteRelay.Server/config.json`
- **Installed (Linux)**: Same directory as the `RemoteRelay.Server` executable
- **Installed (Windows)**: `%ProgramData%\RemoteRelay\Server\config.json`

## Schema Versioning

`config.json` carries a `ConfigVersion` number. At startup the server upgrades
files written by older releases to the current schema automatically, keeping
the original alongside as `config.json.pre-migration`. Files without the key
(pre-versioning releases) are stamped on first load. You don't need to manage
this value; it exists so future releases can change the config shape without
breaking existing installs.

## Relay Driver

The `RelayDriver` key selects which relay hardware the server drives:

| Value | Hardware |
| --- | --- |
| `"Auto"` (default) | Auto-detect: Raspberry Pi GPIO when `/sys/class/gpio` is present, otherwise Mock. |
| `"RpiGpio"` | Raspberry Pi GPIO header (also accepts `"Gpio"`, `"Rpi"`). |
| `"K8090"` | Velleman K8090 / VM8090 8-channel USB relay card (also accepts `"Velleman"`). |
| `"SainSmart"` | SainSmart USB relay module, also compatible with KMtronic (also accepts `"SainsmartUsb"`, `"KMtronic"`). |
| `"LCUS"` | LC Technology / Seeit USB relay module (also accepts `"Seeit"`, `"LCTech"`). |
| `"ModbusRTU"` | Modbus RTU serial relay modules e.g. Waveshare, Dingtian (also accepts `"Modbus"`, `"Waveshare"`). |
| `"Mock"` | No hardware; logs switches only. Useful for testing. |

```json
"RelayDriver": "LCUS",
"LCUS": {
  "Port": "COM3",
  "Channels": 4,
  "BaudRate": 9600
}
```

- If `RelayDriver` is omitted or set to `"Auto"`, the legacy auto-detect behaviour applies (the older `UseMockGpio` flag still forces Mock).
- The driver is applied live: editing `RelayDriver` or driver port settings reloads the driver without restarting the server.

### K8090 (Velleman USB relay card)

Required when `RelayDriver` is `"K8090"`.

- `Port` (string): the serial port the card enumerates as — e.g. `COM3` on Windows, `/dev/ttyACM0` on Linux. The Windows installer offers a dropdown of detected COM ports; the Linux `remoterelay.sh` config menu lists detected serial devices.

With the K8090 driver a route's `RelayPin` is the **relay channel (1–8)** on the card, not a GPIO pin. `ActiveLow` and the `InactiveRelay` polarity settings (`InactiveState`) are GPIO concepts and are **ignored** — the K8090's relays energise when switched on and release when switched off, so wire the normally-open or normally-closed contacts to choose the resting state.

The driver connects to the card on a background thread and keeps hardware and software in step:

- **Auto-reconnect** — if the card is absent at startup, unplugged, or power-cycled, the driver keeps retrying and reconnects on its own. No config reload is needed, and a source switched while the card is offline is remembered and applied the moment it returns.
- **Status polling / drift detection** — the driver periodically reads the card's actual relay states. If they diverge from what RemoteRelay intends (a dropped command, or the card's own on-board buttons/timers), it logs a warning and re-asserts the intended state. Software routing is authoritative.

### SainSmart / KMtronic (USB relay module)

Required when `RelayDriver` is `"SainSmart"` or `"KMtronic"`.

- `Port` (string): the serial port the module enumerates as — e.g. `COM3` on Windows, `/dev/ttyUSB0` or `/dev/serial/by-id/...` on Linux.
- `Channels` (integer, optional, default `4`): the number of relay channels on the board (e.g. `4` for the 4-channel module, `8` for the 8-channel module).
- `BaudRate` (integer, optional, default `9600`): serial communication baud rate (defaults to standard 9600 baud, 8N1).

Uses the standard 3-byte binary command protocol: `[0xFF, (byte)Channel, (byte)(ON=0x01, OFF=0x00)]`. Compatible with SainSmart USB boards and KMtronic USB relay modules.

### LCUS / Seeit (USB relay module)

Required when `RelayDriver` is `"LCUS"`, `"Seeit"`, or `"LCTech"`.

- `Port` (string): the serial port the module enumerates as — e.g. `COM3` on Windows, `/dev/ttyUSB0` or `/dev/serial/by-id/...` on Linux.
- `Channels` (integer, optional, default `4`): number of channels on the board (1, 2, 4, or 8).
- `BaudRate` (integer, optional, default `9600`): baud rate (defaults to 9600 baud, 8N1).

Uses the 4-byte checksummed command protocol: `[0xA0, (byte)Channel, (byte)(ON=0x01, OFF=0x00), (byte)Checksum]`. Compatible with LC Technology (LCUS-1, LCUS-2, LCUS-4, LCUS-8), Seeit (USBB-RELAY04, USBM-RELAY01 from RS Components), NOYITO, HiLetgo, and Diymore boards.

### Modbus RTU (Industrial / Waveshare relay module)

Required when `RelayDriver` is `"ModbusRTU"`, `"Modbus"`, or `"Waveshare"`.

- `Port` (string): the serial port the module enumerates as — e.g. `COM3` on Windows, `/dev/ttyUSB0` or `/dev/serial/by-id/...` on Linux.
- `Channels` (integer, optional, default `8`): number of channels on the board.
- `BaudRate` (integer, optional, default `9600`): baud rate (defaults to 9600 baud, 8N1).
- `SlaveId` (integer, optional, default `1`): Modbus RTU device slave address.

Uses standard Modbus RTU Function 0x05 (Write Single Coil) with 16-bit CRC checksumming. Compatible with Waveshare USB/RS485 relay modules, Dingtian, and commercial DIN-rail relay banks.

## Complete Configuration Example

```json
{
  "ConfigVersion": 1,
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
  "RelayDriver": "Auto",
  "TcpMirrorAddress": null,
  "TcpMirrorPort": null,
  "InactiveRelay": {
    "Pin": 25,
    "InactiveState": "High"
  },
  "FlashOnSelect": true,
  "ShowIpOnScreen": true,
  "ShowClockOnScreen": true,
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
- `RelayPin` (integer): The relay this route controls. With the GPIO driver this is the GPIO pin number (BCM numbering); with the [K8090 driver](#k8090-velleman-usb-relay-card) it is the relay channel (`1`–`8`); with the [SainSmart driver](#sainsmart-usb-relay-module) it is the relay channel (`1`–`4`, or configured `Channels`).
- `ActiveLow` (boolean): GPIO driver only (ignored by K8090 and SainSmart)
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

### ShowClockOnScreen (Optional)

Whether to display the date and time at the top of the client screen. Useful to disable on devices without a reliable NTP/RTC source where the displayed time would be inaccurate.

```json
"ShowClockOnScreen": true
```

- `true`: Show the date and clock
- `false`: Hide the date and clock
- Default: `true`

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
- `RelayDriver` is not a recognised value
- The driver is `K8090` but a route channel or the inactive relay is outside `1`–`8`

A missing `K8090.Port` is a **warning**, not an error: the server still starts and serves its UI/API, and switching becomes operative as soon as a valid port is set (the change is picked up live).

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
