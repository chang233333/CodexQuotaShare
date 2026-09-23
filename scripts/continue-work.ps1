[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
$TaskFile = Join-Path $ProjectDirectory 'task.md'
$LogDirectory = 'E:\codexdefault\CodexQuotaShare\logs'
$LogFile = Join-Path $LogDirectory 'continue-work.log'
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
function Write-ContinueLog([string]$Message) {
    Add-Content -LiteralPath $LogFile -Value ((Get-Date).ToString('o') + ' ' + $Message)
}

Write-ContinueLog 'START'
$HandoffPrefix = ([char]0x4EA4).ToString() + ([char]0x63A5).ToString()
$HandoffDirectory = Join-Path $ProjectDirectory $HandoffPrefix
$LatestHandoff = Get-ChildItem -LiteralPath $HandoffDirectory -Filter '*.md' -File |
    Where-Object { $_.BaseName.StartsWith($HandoffPrefix, [StringComparison]::Ordinal) } |
    Sort-Object { $suffix = $_.BaseName.Substring($HandoffPrefix.Length); if ($suffix -match '^P(\d+)$') { [int]$Matches[1] } else { -1 } } -Descending |
    Select-Object -First 1

$Arguments = @('--reuse-window', $ProjectDirectory, $TaskFile, (Join-Path $ProjectDirectory 'docs\implementation-plan.md'))
if ($LatestHandoff) { $Arguments += $LatestHandoff.FullName }

$CodeCommand = Get-Command code.cmd -ErrorAction SilentlyContinue
if (-not $CodeCommand) { $CodeCommand = Get-Command code -ErrorAction SilentlyContinue }
if ($CodeCommand) {
    Write-ContinueLog ('OPEN_CODE ' + $CodeCommand.Source)
    Start-Process -FilePath $CodeCommand.Source -ArgumentList $Arguments -WindowStyle Normal
} else {
    # The schedule remains useful on machines without VS Code: open the project and task files.
    Write-ContinueLog 'OPEN_FALLBACK explorer-notepad'
    Start-Process -FilePath 'explorer.exe' -ArgumentList @($ProjectDirectory) -WindowStyle Normal
    Start-Process -FilePath 'notepad.exe' -ArgumentList @($TaskFile) -WindowStyle Normal
    if ($LatestHandoff) { Start-Process -FilePath 'notepad.exe' -ArgumentList @($LatestHandoff.FullName) -WindowStyle Normal }
}
Write-ContinueLog 'DONE'
