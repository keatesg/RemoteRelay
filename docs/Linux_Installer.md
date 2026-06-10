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
   release and runs it.

> **x64 note:** on a PC/NUC the client runs normally, and the server works with
> the **K8090 USB** relay driver (or Mock). The Raspberry Pi GPIO driver is
> unavailable on x86_64, so leave the relay driver on `Auto`/`Mock` unless you
> have a K8090.

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
  - **Relay driver** — `Auto`, `RpiGpio`, `K8090`, or `Mock`; for K8090 you can
    pick the serial port from the detected devices.
  - **UDP control API port** — enable/disable the UDP switching API.
  - **Default source** — the source selected automatically when idle.
  - **Client connection** — auto-discover the server or use a fixed IP/hostname.
  - **Screen blanking** — toggle kiosk (always-on) display behaviour.
  - **NTP servers** — set custom time servers.
- **Update & maintenance** — install the latest stable or pre-release, reinstall
  (repair), or uninstall.
- **Logs** — view recent server logs, follow them live, or open the client log.

> Routes, sources, output names, colours, port, inactive relay, TCP mirror and
> the display options are edited from the **client's Setup screen**, which writes
> them straight to the server. The `remoterelay` Configure menu deliberately
> covers only the settings the client cannot reach.

### Non-interactive commands

Every action is also a subcommand, handy for scripts and SSH:

```bash
sudo remoterelay status
sudo remoterelay restart
sudo remoterelay update            # stable
sudo remoterelay update --pre-release
sudo remoterelay update --force    # reinstall current version (repair)
sudo remoterelay uninstall
```

## Updating

```bash
sudo remoterelay update
```

The updater checks GitHub, compares the installed version against the latest
release for your configured channel, backs up your config (keeping the last 3
backups under `~/.remoterelay-backups`), downloads the right installer for your
architecture, and runs it. Your configuration is preserved.

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
| `/etc/systemd/system/remote-relay-server.service` | Server service unit |
| `~/.remoterelay-backups` | Pre-update config backups |
