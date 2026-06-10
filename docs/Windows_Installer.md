# Windows Installer

RemoteRelay ships a Windows MSI (`RemoteRelay-Setup.msi`) built with [WiX v5](https://wixtoolset.org/). It bundles a self-contained .NET 10 publish of both apps, so no runtime needs to be installed on the target machine.

## What the installer does

The installer presents a feature-selection tree so you can install the **Client**, the **Server**, or both:

| Feature | Installed to | Notes |
|---|---|---|
| RemoteRelay Client | `C:\Program Files\RemoteRelay\Client` | Avalonia desktop GUI. Start Menu shortcut created. |
| RemoteRelay Server | `C:\Program Files\RemoteRelay\Server` | Installed as a **Windows Service** (`RemoteRelayServer`) that starts automatically at boot. An inbound TCP firewall rule for the chosen port is created. |

After feature selection, the wizard prompts for a small set of configuration values.

### Server configuration page (only shown if Server is selected)

| Field | Maps to | Default |
|---|---|---|
| Server port | `ServerPort` in `config.json` | 33101 |
| Relay driver | `RelayDriver` | `K8090` |
| K8090 serial port | `K8090.Port` | first auto-detected COM port |
| Logo file | `LogoFile` | empty (no logo served at `/logo`) |

Available COM ports are enumerated at install time via `System.IO.Ports.SerialPort.GetPortNames()`.

### Client configuration page (only shown if Client is selected)

| Field | Maps to | Default |
|---|---|---|
| Server host or IP | `Host` in `ClientConfig.json` | `localhost` |
| Server port | `Port` | 33101 |

## Where the configuration files end up

- Server: `C:\Program Files\RemoteRelay\Server\config.json`
- Client: `C:\Program Files\RemoteRelay\Client\ClientConfig.json`

Both files are **only written if they don't already exist**, so reinstalls and upgrades won't overwrite hand-edited configuration. To regenerate them, uninstall and reinstall, or delete the file before reinstalling.

## Updating

The MSI's `ProductVersion` is stamped from the release tag in CI, and the
package uses `MajorUpgrade`, so installing a newer `RemoteRelay-Setup.msi`
performs a clean in-place upgrade: the service is stopped, files are replaced,
configuration is preserved, and the service is restarted. Downgrades are blocked
with a clear message. (Local builds default to version `1.0.0.0` unless you pass
`-p:BuildVersion=<x.y.z>`.)

For the full schema of each file, see [Server_Configuration.md](Server_Configuration.md) and [Client_Configuration.md](Client_Configuration.md). The installer wizard only captures the most common fields; everything else can be edited directly in the JSON files after install. The server's `ConfigurationWatcher` picks up changes to `config.json` without restarting the service.

## Managing the Server service

```powershell
# Status
Get-Service RemoteRelayServer

# Stop / start / restart
Stop-Service RemoteRelayServer
Start-Service RemoteRelayServer
Restart-Service RemoteRelayServer

# Logs go to the install folder
Get-Content 'C:\Program Files\RemoteRelay\Server\server_error.log' -Tail 50
```

The service runs as **LocalSystem** so it has access to COM ports for the K8090 driver without extra ACL configuration.

## Building the installer locally

You'll need the .NET 10 SDK and the WiX 5 toolset. The Visual Studio extension is not required — the WiX 5 SDK builds via `dotnet build`.

```powershell
# 1. Publish self-contained Client + Server
dotnet publish RemoteRelay/RemoteRelay.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -o publish/client

dotnet publish RemoteRelay.Server/RemoteRelay.Server.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -o publish/server

# 2. Build the DTF custom-action assembly
dotnet build RemoteRelay.Installer.CustomActions/RemoteRelay.Installer.CustomActions.csproj -c Release

# 3. Build the MSI
dotnet build RemoteRelay.Installer/RemoteRelay.Installer.wixproj -c Release
```

The resulting `RemoteRelay-Setup.msi` lands in `RemoteRelay.Installer\bin\Release\`.

## CI

The Windows installer is built on `windows-latest` by the `windows-installer` job in [`.github/workflows/dotnet.yml`](../.github/workflows/dotnet.yml). The MSI is uploaded as the `RemoteRelay-Installer-windows` artifact and is attached to GitHub Releases by [`.github/workflows/release.yml`](../.github/workflows/release.yml).

## Known limitations

- The MSI is **not code-signed** in this iteration. Windows SmartScreen will show a warning on first run; users will need to click "More info" → "Run anyway". Signing can be added by providing a certificate in CI secrets and running `signtool` against the produced MSI.
- The installer is x64-only. There is no win-arm64 build.
- The wizard does not let you configure GPIO `Routes` or `PhysicalSourceButtons`. Edit `config.json` directly for those — see [Server_Configuration.md](Server_Configuration.md).
