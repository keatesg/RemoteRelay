<#
.SYNOPSIS
    Management helper for the RemoteRelay Server Windows service.

    Launched by the Start Menu shortcuts created by the installer. Each shortcut
    passes a different -Action. Actions that change service state self-elevate via
    UAC; read-only actions (editing the user-writable config, viewing logs) do not.
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Start', 'Stop', 'Restart', 'EditConfig', 'Logs')]
    [string]$Action = 'Status'
)

$ServiceName = 'RemoteRelayServer'
$ConfigPath  = Join-Path $env:ProgramData 'RemoteRelay\Server\config.json'
$LogPath     = Join-Path $env:ProgramData 'RemoteRelay\Server\server_error.log'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Elevated([string]$action) {
    # Re-launch this script elevated for the given action, then wait so the user sees the result.
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"", '-Action', $action
    )
}

switch ($Action) {
    'EditConfig' {
        if (-not (Test-Path $ConfigPath)) {
            Write-Host "Config file not found at $ConfigPath" -ForegroundColor Yellow
        }
        # config.json is user-writable (the installer grants Users Modify), so no elevation needed.
        Start-Process notepad.exe -ArgumentList "`"$ConfigPath`""
        return
    }
    'Logs' {
        if (Test-Path $LogPath) {
            Start-Process notepad.exe -ArgumentList "`"$LogPath`""
        } else {
            Write-Host "No log file yet at $LogPath" -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
        return
    }
    default {
        # Start / Stop / Restart / Status — service control needs admin.
        if ($Action -ne 'Status' -and -not (Test-Admin)) {
            Invoke-Elevated $Action
            return
        }

        switch ($Action) {
            'Start'   { Start-Service   $ServiceName }
            'Stop'    { Stop-Service    $ServiceName }
            'Restart' { Restart-Service $ServiceName }
        }

        $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
        } else {
            Write-Host "RemoteRelay Server: $($svc.Status) (startup: $($svc.StartType))" -ForegroundColor Cyan
            try {
                $health = Invoke-RestMethod 'http://localhost:33101/health' -TimeoutSec 3
                Write-Host "Health: $($health.status), version $($health.version)" -ForegroundColor Green
            } catch {
                Write-Host "Health endpoint not responding on :33101." -ForegroundColor Yellow
            }
        }
        Write-Host ''
        Read-Host 'Press Enter to close'
    }
}
