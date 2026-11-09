# Restores the original winamp.exe from the backup made by apply-fix.ps1.
# Run from an elevated PowerShell prompt, with Winamp closed.

[CmdletBinding()]
param(
    [string]$WinampDir = (Join-Path ${env:ProgramFiles(x86)} 'Winamp')
)

$ErrorActionPreference = 'Stop'

$OriginalHash = 'ADDF561FCDC496C1318DDC3586352AA7F6C1FEB684A9E8FFA285409BEAC5B446'
$FixedHash = 'CC98F8C138D75B4A2843E64EF5B0F2891509C400038A5768A4EC5A3537D42040'

$Exe = Join-Path $WinampDir 'winamp.exe'
$Backup = Join-Path $WinampDir 'winamp.exe.original-5.9.2.10042.bak'

function Stop-WithMessage([string]$Message) {
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    exit 1
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

if (-not (Test-Path -LiteralPath $Exe)) { Stop-WithMessage "winamp.exe not found in $WinampDir" }

$running = Get-Process -Name winamp -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Resolve-Path -LiteralPath $Exe).Path }
if ($running) { Stop-WithMessage 'Winamp is running. Close it and try again.' }

$current = Get-Sha256 $Exe
if ($current -eq $OriginalHash) {
    Write-Host 'winamp.exe is already the original. Nothing was changed.' -ForegroundColor Green
    exit 0
}
if ($current -ne $FixedHash) {
    Stop-WithMessage "This winamp.exe is neither the original nor the fixed file (SHA-256 $current). Nothing was changed."
}
if (-not (Test-Path -LiteralPath $Backup)) { Stop-WithMessage "Backup not found: $Backup" }
if ((Get-Sha256 $Backup) -ne $OriginalHash) { Stop-WithMessage 'The backup is not the original file. Nothing was changed.' }

try {
    Copy-Item -LiteralPath $Backup -Destination $Exe -Force
} catch [System.UnauthorizedAccessException] {
    Stop-WithMessage 'Access denied. Run PowerShell as administrator.'
}

if ((Get-Sha256 $Exe) -ne $OriginalHash) { Stop-WithMessage 'winamp.exe could not be restored correctly. The backup is still in place.' }

Write-Host 'Original winamp.exe restored.' -ForegroundColor Green
