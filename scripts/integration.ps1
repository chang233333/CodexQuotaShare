[CmdletBinding()]
param([string]$TaskDirectory = 'E:\codexdefault\CodexQuotaShare', [string]$SdkDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $SdkDirectory) { $SdkDirectory = Join-Path $TaskDirectory 'toolchains/dotnet' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $TaskDirectory ('artifacts/integration-' + [Guid]::NewGuid().ToString('N')) }
$Saved = @{}
$Overrides = @{ DOTNET_ROOT = $SdkDirectory; TEMP = (Join-Path $TaskDirectory 'tmp'); TMP = (Join-Path $TaskDirectory 'tmp'); SystemRoot = 'C:\Windows'; ComSpec = 'C:\Windows\System32\cmd.exe' }
try {
    foreach ($Entry in $Overrides.GetEnumerator()) { $Saved[$Entry.Key] = [Environment]::GetEnvironmentVariable($Entry.Key, 'Process'); [Environment]::SetEnvironmentVariable($Entry.Key, $Entry.Value, 'Process') }
    Push-Location (Join-Path $ProjectDirectory 'relay')
    try {
        $Arguments = @('tests/client-integration.mjs', (Join-Path $SdkDirectory 'dotnet.exe'), (Join-Path $ProjectDirectory 'tests/CodexQuotaShare.Core.Tests/bin/Release/net10.0/CodexQuotaShare.Core.Tests.dll'), $OutputDirectory)
        $CommandLine = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
        $Process = Start-Process -FilePath (Get-Command node.exe).Source -ArgumentList $CommandLine -Wait -PassThru -NoNewWindow
        if ($Process.ExitCode -ne 0) { throw "C# / Worker integration failed: node exit $($Process.ExitCode)." }
    } finally { Pop-Location }
} finally { foreach ($Entry in $Saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($Entry.Key, $Entry.Value, 'Process') } }
