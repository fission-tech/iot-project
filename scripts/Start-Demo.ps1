# Starts the demo console, which then starts, stops and crashes every component from one page.
param([switch]$NoBuild, [switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $workspace
try {
    if (-not (Test-Path -LiteralPath (Join-Path $workspace 'appsettings.json'))) {
        throw 'Create root appsettings.json using the settings in README.md.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $workspace '.env'))) {
        throw 'Copy .env.example to .env and set the passwords (TimescaleDB and Grafana).'
    }
    if (-not $NoBuild) {
        dotnet build IoT.sln --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    # Through cmd so Windows PowerShell does not treat docker's progress output on stderr as an error.
    cmd /c 'docker compose up -d --wait 2>&1'
    if ($LASTEXITCODE -ne 0) { throw 'Container startup failed (Mosquitto, TimescaleDB, Grafana). Is Docker Desktop running?' }
    $url = 'http://127.0.0.1:5200'
    if (-not $NoBrowser) {
        Start-Job -ScriptBlock { param($u) Start-Sleep -Seconds 3; Start-Process $u } -ArgumentList $url | Out-Null
    }
    Write-Output "Demo console: $url (press Ctrl+C here to stop it and every component it started)."
    dotnet (Join-Path $workspace 'src/Demo.Control/bin/Debug/net8.0/Demo.Control.dll')
}
finally {
    Pop-Location
}
