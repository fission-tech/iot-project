<#
.SYNOPSIS
Creates Multipass VMs that act as qbee staging devices: Ubuntu Server with Docker and the qbee agent.

.DESCRIPTION
For each name, launches the VM if it doesn't exist, then runs setup-device.sh inside it. The qbee bootstrap key
comes from the QBEE_BOOTSTRAP_KEY environment variable or bootstrap-key.txt next to this script (gitignored).
Without a key, only Docker is installed. Running it again on an existing VM skips what is already done.

.EXAMPLE
.\new-staging-devices.ps1                      # qbee-staging-1
.\new-staging-devices.ps1 -Count 3             # qbee-staging-1, -2 and -3
.\new-staging-devices.ps1 -Name lab-gw-01      # any names
#>
param(
    [int]$Count = 1,
    [string]$Prefix = "qbee-staging-",
    [string[]]$Name,
    [string]$Image = "24.04",
    [int]$Cpus = 2,
    [string]$Memory = "1G",
    [string]$Disk = "10G"
)
$ErrorActionPreference = "Stop"

if (-not $Name) { $Name = 1..$Count | ForEach-Object { "$Prefix$_" } }

$key = $env:QBEE_BOOTSTRAP_KEY
$keyFile = Join-Path $PSScriptRoot "bootstrap-key.txt"
if (-not $key -and (Test-Path $keyFile)) { $key = (Get-Content $keyFile -TotalCount 1).Trim() }
if (-not $key) { Write-Warning "No qbee bootstrap key (QBEE_BOOTSTRAP_KEY or $keyFile): installing Docker only." }

# Multipass copies the script into the VM; it must have Unix line endings whatever the checkout has.
$setup = Join-Path $env:TEMP "qbee-setup-device.sh"
$text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "setup-device.sh")) -replace "`r`n", "`n"
[IO.File]::WriteAllText($setup, $text, (New-Object Text.UTF8Encoding $false))

# The key goes in as a file the setup script deletes, so it isn't in a command line or the VM's shell history.
$keyCopy = Join-Path $env:TEMP "qbee-bootstrap-key"
if ($key) { [IO.File]::WriteAllText($keyCopy, $key) }

$instances = @((multipass list --format json | ConvertFrom-Json).list)
try {
    foreach ($vm in $Name) {
        $instance = $instances | Where-Object { $_.name -eq $vm }
        if (-not $instance) {
            Write-Host "== $vm`: launching ($Image, $Cpus CPUs, $Memory memory, $Disk disk); takes a few minutes the first time"
            multipass launch $Image --name $vm --cpus $Cpus --memory $Memory --disk $Disk
        } elseif ($instance.state -ne "Running") {
            Write-Host "== $vm`: starting (was $($instance.state))"
            multipass start $vm
        } else {
            Write-Host "== $vm`: already running"
        }
        if ($LASTEXITCODE -ne 0) { throw "Multipass could not start $vm" }

        Write-Host "== $vm`: copying the setup script"
        multipass transfer $setup "${vm}:/tmp/setup-device.sh"
        if ($LASTEXITCODE -ne 0) { throw "Could not copy the setup script to $vm" }
        $keyArg = @()
        if ($key) {
            multipass transfer $keyCopy "${vm}:/tmp/qbee-bootstrap-key"
            if ($LASTEXITCODE -ne 0) { throw "Could not copy the bootstrap key to $vm" }
            $keyArg = @("/tmp/qbee-bootstrap-key")
        }

        Write-Host "== $vm`: running setup (installing Docker takes a few minutes)"
        multipass exec $vm -- sudo bash /tmp/setup-device.sh @keyArg
        if ($LASTEXITCODE -ne 0) { throw "Setup failed on $vm" }
    }
} finally {
    Remove-Item $setup, $keyCopy -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. In qbee: approve the device(s) under Devices -> Pending Devices, move them into the staging group,"
Write-Host "and give each device the parameter GATEWAY_ID."
