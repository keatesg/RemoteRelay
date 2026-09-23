# Linux Installer & Management

RemoteRelay on Raspberry Pi / Linux installs with a single command and is
managed afterwards with the `remoterelay` tool. Both present a friendly terminal
UI (whiptail dialogs where available, plain text otherwise).

## Installing

```bash
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash
```

The bootstrap (`get.sh`):

1. Checks it is running as root.
2. Installs prerequisites if missing (`curl`, `jq`, and `whiptail` for the UI).
3. Detects the CPU architecture and picks the matching build:
   `aarch64` → `linux-arm64`, `armv7l`/`armv6l` → `linux-arm`,
   `x86_64` → `linux-x64`.
4. Downloads the matching self-extracting installer from the latest GitHub
   release, verifies it against the release's `SHA256SUMS` manifest, and runs
   it. (Releases published before checksums were introduced skip verification
   with a warning.)

> **x64 note:** on a PC/NUC the client runs normally, and the server works with
> the **K8090 USB** or **SainSmart USB** relay driver (or Mock). The Raspberry Pi GPIO driver is
> unavailable on x86_64, so leave the relay driver on `Auto`/`Mock` unless you
> have a USB relay device.

### Options

Pass flags after `-- ` when piping into bash:

| Flag | Effect |
|------|--------|
| `--pre-release` | Install the latest pre-release instead of stable |
| `--unattended` | Never prompt; install both components with safe defaults |
| `--server-only` | Install/Update only the server |
| `--client-only` | Install/Update only the client |

```bash
curl -fsSL https://raw.githubusercontent.com/keatesg/RemoteRelay/master/get.sh | sudo bash -s -- --pre-release
```

If there is no interactive terminal (e.g. an automated provisioner pipes the
script in with no TTY), the installer automatically runs unattended with
defaults rather than blocking on a prompt.

### Manual / offline install

Every release also attaches the self-extracting installers used under the hood:

```bash
chmod +x RemoteRelay-Installer-linux-arm64.sh
sudo ./RemoteRelay-Installer-linux-arm64.sh
```

Assets are published per architecture: `RemoteRelay-Installer-linux-arm64.sh`
(Pi 64-bit), `RemoteRelay-Installer-linux-arm.sh` (Pi 32-bit), and
`RemoteRelay-Installer-linux-x64.sh` (PC/NUC).

## What the installer sets up

- Installs the chosen components to `~/RemoteRelay/{server,client}`.
- Registers the server as a systemd service (`remote-relay-server.service`) that
  starts at boot.
- Configures client auto-start (Wayfire on modern Raspberry Pi OS, plus an XDG
  entry for X11) and an optional kiosk mode that disables screen blanking.
- Preserves existing configuration files across re-installs and updates.
- Installs the `remoterelay` management command to `/usr/local/bin`.

## Managing your installation

Run the management tool at any time:

```bash
sudo remoterelay
```

The menu provides:

- **Status** — service state, installed versions, listen port, IP addresses,
  relay driver, and client auto-start state.
- **Service** — start / stop / restart / enable / disable the server.
- **Configure** — the options that are *not* set from the client's Setup screen:
  - **Relay driver** — `Auto`, `RpiGpio`, `K8090`, `SainSmart`, or `Mock`; for K8090 and
    SainSmart you can pick the serial port from the detected devices.
  - **UDP control API port** — enable/disable the UDP switching API.
  - **Default source** — the source selected automatically when idle.
  - **Client connection** — auto-discover the server or use a fixed IP/hostname.
  - **Screen blanking** — toggle kiosk (always-on) display behaviour.
  - **NTP servers** — set custom time servers.
- **Update & maintenance** — install the latest stable or pre-release, reinstall
  (repair), roll back to the previously installed version, or uninstall.
- **Logs** — view recent server logs, follow them live, or open the client log.

## Debian (.deb) Packages

Debian packages are provided for Raspberry Pi OS and Debian-based systems:

- `remoterelay-server_<version>_<arch>.deb`:
  - Contains the **RemoteRelay Server** daemon (`/usr/lib/remoterelay/server/RemoteRelay.Server` symlinked to `/usr/bin/remoterelay-server`).
  - Contains the **RemoteRelay Configurator** GUI (`/usr/lib/remoterelay/configurator/RemoteRelay.Configurator` symlinked to `/usr/bin/remoterelay-config`).
  - Automatically installs and enables the systemd service `remote-relay-server.service`.
  - Installs `/etc/remoterelay/config.json` as a package `conffile` (upgrades never overwrite your edits).
  - Installs the Desktop launcher for the Configurator.
- `remoterelay-client_<version>_<arch>.deb`:
  - Contains the **RemoteRelay Touch Client** (`/usr/lib/remoterelay/client/RemoteRelay` symlinked to `/usr/bin/remoterelay-client`).
  - Installs the Desktop launcher for the Client.

### Installing Debian Packages

```bash
# Install Server (and Configurator)
sudo apt install ./remoterelay-server_*.deb

# Install Touch Client
sudo apt install ./remoterelay-client_*.deb
```

> **Configuration Note:** All routing, hardware drivers, pins, and relay configuration belong strictly on the server (`/etc/remoterelay/config.json`). You can edit this file directly with any text editor (the server automatically hot-reloads it), or use the **RemoteRelay Configurator** desktop app (`remoterelay-config`). The touch client is dedicated to switching operations, server connection configuration, and local display filtering (showing/hiding specific inputs or outputs).

### Non-interactive commands

Every action is also a subcommand, handy for scripts and SSH:

```bash
sudo remoterelay status
sudo remoterelay restart
sudo remoterelay update            # stable
sudo remoterelay update --pre-release
sudo remoterelay update --force    # reinstall current version (repair)
sudo remoterelay update --version v1.2.3   # install a specific release
sudo remoterelay rollback          # return to the previously installed version
sudo remoterelay uninstall
```

## Updating

```bash
sudo remoterelay update
```

The updater checks GitHub, compares the installed version against the latest
release for your configured channel, backs up your config (keeping the last 3
backups under `~/.remoterelay-backups`), downloads the right installer for your
architecture, verifies its checksum against the release's `SHA256SUMS`
manifest, and runs it. Your configuration is preserved.

`sudo remoterelay update --version v1.2.3` installs a specific release instead
of the channel's latest — downgrades included.

## Rolling back

```bash
sudo remoterelay rollback
```

Every update first records which version it is replacing and keeps a backup of
its configuration. `rollback` reinstalls that version and restores that config
backup — useful when a new release misbehaves. Rolling back records the same
metadata, so a rollback can itself be undone by running `rollback` again (or
`update` to return to the latest).

## Uninstalling

```bash
sudo remoterelay uninstall
```

This stops and removes the service, deletes the installed files, reverts
auto-start/kiosk changes, and — when both components are removed — also removes
the `remoterelay` tool, its libraries (`/usr/local/lib/remoterelay`), and the
install metadata (`/etc/remoterelay`).

## Files & locations

| Path | Purpose |
|------|---------|
| `~/RemoteRelay/server` | Server binaries + `config.json` |
| `~/RemoteRelay/client` | Client binaries + `ClientConfig.json` |
| `~/RemoteRelay/{update,uninstall}.sh` | Updater / uninstaller |
| `/usr/local/bin/remoterelay` | Management tool |
| `/usr/local/lib/remoterelay` | Shared UI libraries |
| `/etc/remoterelay/install.conf` | Install metadata (user, paths, repo, channel) |
| `/etc/remoterelay/rollback.conf` | Version and config backup the last update replaced |
| `/etc/systemd/system/remote-relay-server.service` | Server service unit |
| `~/.remoterelay-backups` | Pre-update config backups |
