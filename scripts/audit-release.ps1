[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ExtractionDirectory
)

$ErrorActionPreference = 'Stop'
$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
$ExtractionDirectory = [System.IO.Path]::GetFullPath($ExtractionDirectory)
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw "Archive not found: $ArchivePath" }
if (Test-Path -LiteralPath $ExtractionDirectory) { throw "Extraction directory already exists: $ExtractionDirectory" }

$root = Split-Path -Parent $ArchivePath
$sumPath = Join-Path $root 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sumPath -PathType Leaf)) { throw "Checksum file not found: $sumPath" }
$actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumLine = Get-Content -LiteralPath $sumPath | Where-Object { $_ -match [regex]::Escape((Split-Path -Leaf $ArchivePath)) } | Select-Object -First 1
if (-not $sumLine) { throw "Checksum entry not found for $ArchivePath" }
$expectedHash = (($sumLine -split '\s+')[0]).ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw "SHA256 mismatch: expected $expectedHash, actual $actualHash" }

New-Item -ItemType Directory -Path $ExtractionDirectory -Force | Out-Null
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractionDirectory -Force
$required = @('CodexQuotaShare.exe', 'README.md', 'PRIVACY.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $ExtractionDirectory $name) -PathType Leaf)) { throw "Missing package file: $name" }
}
if (Test-Path -LiteralPath (Join-Path $ExtractionDirectory 'data')) { throw 'Package created a local data directory before first launch.' }

$resultPath = Join-Path $ExtractionDirectory 'release-smoke.json'
$exePath = Join-Path $ExtractionDirectory 'CodexQuotaShare.exe'
$process = Start-Process -FilePath $exePath -ArgumentList ('--smoke-test "' + $resultPath + '"') -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Release smoke exited with code $($process.ExitCode)" }
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw 'Release smoke did not write a result.' }
$report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if (-not $report.passed) { throw 'Release smoke reported passed=false.' }
$dataAfterSmoke = Test-Path -LiteralPath (Join-Path $ExtractionDirectory 'data')
if ($dataAfterSmoke) { throw 'Release smoke created a local data directory in the clean extraction.' }

[pscustomobject]@{
    archive = $ArchivePath
    extractionDirectory = $ExtractionDirectory
    sha256 = $actualHash
    packageDataDirectoryBeforeLaunch = $false
    packageDataDirectoryAfterSmoke = $dataAfterSmoke
    requiredFiles = $required.Count
    smokePassed = [bool]$report.passed
    relayRendered = [bool]$report.relayRendered
    reopen = [bool]$report.reopen
} | ConvertTo-Json -Compress
