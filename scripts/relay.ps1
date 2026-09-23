[CmdletBinding()]
param(
    [ValidateSet('Install','Typecheck','Test','Build','Dev')][string]$Action = 'Test',
    [string]$TaskDirectory = 'E:\codexdefault\CodexQuotaShare'
)
$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
$Overrides = @{
    npm_config_cache = (Join-Path $TaskDirectory 'npm-cache')
    WRANGLER_HOME = (Join-Path $TaskDirectory 'wrangler-home')
    XDG_CONFIG_HOME = (Join-Path $TaskDirectory 'config')
    WRANGLER_SEND_METRICS = 'false'
    WRANGLER_LOG_PATH = (Join-Path $TaskDirectory 'diagnostics\phase3\wrangler.log')
    TEMP = (Join-Path $TaskDirectory 'tmp')
    TMP = (Join-Path $TaskDirectory 'tmp')
    PATHEXT = '.COM;.EXE;.BAT;.CMD'
}
if (-not $env:SystemRoot) { $Overrides['SystemRoot'] = 'C:\Windows' }
if (-not $env:ComSpec) { $Overrides['ComSpec'] = 'C:\Windows\System32\cmd.exe' }
$Previous = @{}
try {
    foreach ($Item in $Overrides.GetEnumerator()) {
        $Previous[$Item.Key] = [Environment]::GetEnvironmentVariable($Item.Key, 'Process')
        [Environment]::SetEnvironmentVariable($Item.Key, $Item.Value, 'Process')
    }
    foreach ($Directory in @('npm-cache','wrangler-home','config','tmp','diagnostics\phase11','artifacts\phase11\relay')) {
        New-Item -ItemType Directory -Path (Join-Path $TaskDirectory $Directory) -Force | Out-Null
    }
    Push-Location (Join-Path $ProjectDirectory 'relay')
    try {
        switch ($Action) {
            'Install' { & npm.cmd ci --no-audit --no-fund }
            'Typecheck' { & npm.cmd run typecheck }
            'Test' { & npm.cmd test }
            'Build' { & npm.cmd run build -- --outdir (Join-Path $TaskDirectory 'artifacts\phase11\relay') }
            'Dev' { & npm.cmd run dev }
        }
        if ($LASTEXITCODE -ne 0) { throw "Relay $Action failed: exit $LASTEXITCODE" }
    } finally { Pop-Location }
} finally {
    foreach ($Item in $Previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($Item.Key, $Item.Value, 'Process') }
}
