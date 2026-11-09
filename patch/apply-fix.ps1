# Applies the playlist crash fix to your own copy of Winamp 5.9.2.10042.
# Run from an elevated PowerShell prompt, with Winamp closed.

[CmdletBinding()]
param(
    [string]$WinampDir = (Join-Path ${env:ProgramFiles(x86)} 'Winamp')
)

$ErrorActionPreference = 'Stop'

$OriginalHash = 'ADDF561FCDC496C1318DDC3586352AA7F6C1FEB684A9E8FFA285409BEAC5B446'
$FixedHash = 'CC98F8C138D75B4A2843E64EF5B0F2891509C400038A5768A4EC5A3537D42040'
$Offset = 0xAD297
$Expected = [byte[]](0xFF, 0x15, 0xD8, 0x76, 0x4E, 0x00)

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
if ($current -eq $FixedHash) {
    Write-Host 'The fix is already applied. Nothing was changed.' -ForegroundColor Green
    exit 0
}
if ($current -ne $OriginalHash) {
    Stop-WithMessage "This winamp.exe is not the supported 5.9.2.10042 build (SHA-256 $current). Nothing was changed."
}

$bytes = [IO.File]::ReadAllBytes($Exe)
for ($i = 0; $i -lt $Expected.Length; $i++) {
    if ($bytes[$Offset + $i] -ne $Expected[$i]) { Stop-WithMessage 'Unexpected bytes at the patch location. Nothing was changed.' }
}

try {
    if (Test-Path -LiteralPath $Backup) {
        if ((Get-Sha256 $Backup) -ne $OriginalHash) { Stop-WithMessage 'A backup file exists but is not the original. Nothing was changed.' }
    } else {
        Copy-Item -LiteralPath $Exe -Destination $Backup
        if ((Get-Sha256 $Backup) -ne $OriginalHash) { Stop-WithMessage 'The backup could not be verified. winamp.exe was not changed.' }
    }

    for ($i = 0; $i -lt $Expected.Length; $i++) { $bytes[$Offset + $i] = 0x90 }
    [IO.File]::WriteAllBytes($Exe, $bytes)

    if ((Get-Sha256 $Exe) -ne $FixedHash) {
        Copy-Item -LiteralPath $Backup -Destination $Exe -Force
        Stop-WithMessage 'The patched file did not match the expected result. The original was restored.'
    }
} catch [System.UnauthorizedAccessException] {
    Stop-WithMessage 'Access denied. Run PowerShell as administrator.'
}

Write-Host 'Fix applied.' -ForegroundColor Green
Write-Host "Backup: $Backup"
