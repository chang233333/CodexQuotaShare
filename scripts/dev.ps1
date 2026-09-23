[CmdletBinding()]
param(
    [ValidateSet('Test','Build','Publish','Smoke')][string]$Action = 'Test',
    [string]$TaskDirectory = 'E:\codexdefault\CodexQuotaShare',
    [string]$SdkDirectory = '',
    [string]$ArtifactDirectory = '',
    [string]$DefaultRelayUrl = '',
    [string]$GitHubRepository = ''
)
$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $SdkDirectory) { $SdkDirectory = Join-Path $TaskDirectory 'toolchains\dotnet' }
$DotnetExe = Join-Path $SdkDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $DotnetExe)) { throw "SDK not found: $DotnetExe" }
$ClientProject = Join-Path $ProjectDirectory 'client\CodexQuotaShare.Windows\CodexQuotaShare.Windows.csproj'
$TestProject = Join-Path $ProjectDirectory 'tests\CodexQuotaShare.Core.Tests\CodexQuotaShare.Core.Tests.csproj'
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $TaskDirectory 'artifacts\phase2' }
$PublishDirectory = Join-Path $ArtifactDirectory 'publish'
$EnvironmentOverrides = @{
    DOTNET_ROOT = $SdkDirectory
    DOTNET_CLI_HOME = (Join-Path $TaskDirectory 'dotnet-home')
    NUGET_PACKAGES = (Join-Path $TaskDirectory 'nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $TaskDirectory 'nuget\http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $TaskDirectory 'nuget\plugins')
    NUGET_COMMON_APPLICATION_DATA = (Join-Path $TaskDirectory 'nuget\machine')
    APPDATA = (Join-Path $TaskDirectory 'profile\roaming')
    LOCALAPPDATA = (Join-Path $TaskDirectory 'profile\local')
    PROGRAMDATA = (Join-Path $TaskDirectory 'profile\programdata')
    ALLUSERSPROFILE = (Join-Path $TaskDirectory 'profile\programdata')
    TEMP = (Join-Path $TaskDirectory 'tmp')
    TMP = (Join-Path $TaskDirectory 'tmp')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_NOLOGO = '1'
    DOTNET_MULTILEVEL_LOOKUP = '0'
}
# Some agent hosts omit these process variables. Supply standard runtime paths only when missing.
foreach ($Name in @('ProgramFiles','ProgramFiles(x86)','ProgramW6432')) {
    if (-not [Environment]::GetEnvironmentVariable($Name, 'Process')) {
        $EnvironmentOverrides[$Name] = if ($Name -eq 'ProgramFiles(x86)') { 'C:\Program Files (x86)' } else { 'C:\Program Files' }
    }
}
if (-not $env:PATHEXT) { $EnvironmentOverrides['PATHEXT'] = '.COM;.EXE;.BAT;.CMD' }
$PreviousEnvironment = @{}
function Invoke-Dotnet {
    param([string[]]$Arguments)
    # Start-Process joins ArgumentList values into one command line and does not quote
    # paths containing spaces, so quote each argument explicitly before launching.
    $CommandLine = ($Arguments | ForEach-Object {
        $Value = [string]$_
        if ($Value -match '[\s"]') { '"' + $Value.Replace('"', '\"') + '"' } else { $Value }
    }) -join ' '
    $Process = Start-Process -FilePath $DotnetExe -ArgumentList $CommandLine -Wait -PassThru -NoNewWindow
    if ($Process.ExitCode -ne 0) { throw "dotnet failed: exit $($Process.ExitCode)" }
}
try {
    foreach ($Entry in $EnvironmentOverrides.GetEnumerator()) {
        $PreviousEnvironment[$Entry.Key] = [Environment]::GetEnvironmentVariable($Entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($Entry.Key, $Entry.Value, 'Process')
    }
    foreach ($Path in @('dotnet-home','nuget\packages','nuget\http-cache','nuget\plugins','nuget\machine','profile\roaming','profile\local','profile\programdata','tmp')) {
        New-Item -ItemType Directory -Path (Join-Path $TaskDirectory $Path) -Force | Out-Null
    }
    Push-Location $ProjectDirectory
    try {
        switch ($Action) {
            'Test' { Invoke-Dotnet -Arguments @('run','--project',$TestProject,'-c','Release') }
            'Build' { Invoke-Dotnet -Arguments @('build',$ClientProject,'-c','Release') }
            'Publish' {
                if ($DefaultRelayUrl -and $DefaultRelayUrl -notmatch '^https://[^/]+/?$') { throw 'DefaultRelayUrl must be an HTTPS origin.' }
                if ($GitHubRepository -and $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHubRepository must be owner/repo.' }
                Invoke-Dotnet -Arguments @('publish',$ClientProject,'-c','Release','-r','win-x64','--self-contained','true','-o',$PublishDirectory)
                @{ DefaultRelayUrl = $(if ($DefaultRelayUrl) { $DefaultRelayUrl } else { $null }); GitHubRepository = $(if ($GitHubRepository) { $GitHubRepository } else { $null }) } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PublishDirectory 'release-config.json') -Encoding UTF8
                foreach ($Resource in @('CodexQuotaShare.pri','App.xbf')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $Resource))) { throw "Missing WinUI publish resource: $Resource" }
                }
                foreach ($File in @('LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $ProjectDirectory $File) -Destination $PublishDirectory -Force }
                Copy-Item -LiteralPath (Join-Path $ProjectDirectory 'licenses') -Destination $PublishDirectory -Recurse -Force
                Copy-Item -LiteralPath (Join-Path $ProjectDirectory 'docs\phase1-user-guide.md') -Destination (Join-Path $PublishDirectory 'README.md') -Force
                Copy-Item -LiteralPath (Join-Path $ProjectDirectory 'docs\privacy.md') -Destination (Join-Path $PublishDirectory 'PRIVACY.md') -Force
                foreach ($Required in @('CodexQuotaShare.exe', 'README.md', 'PRIVACY.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $Required))) { throw "Missing publish file: $Required" }
                }
                if (Test-Path -LiteralPath (Join-Path $PublishDirectory 'data')) { throw 'Publish contains local data; use a fresh task output directory before packaging.' }
                $LicenseDirectory = Join-Path $PublishDirectory 'dependency-licenses'
                New-Item -ItemType Directory -Path $LicenseDirectory -Force | Out-Null
                $Assets = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'client\CodexQuotaShare.Windows\obj\project.assets.json') -Raw | ConvertFrom-Json
                foreach ($Library in $Assets.libraries.PSObject.Properties) {
                    if ($Library.Value.type -ne 'package') { continue }
                    $PackageDirectory = Join-Path $EnvironmentOverrides.NUGET_PACKAGES $Library.Value.path
                    $Destination = Join-Path $LicenseDirectory ($Library.Name -replace '/', '-')
                    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
                    Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object { $_.Name -match 'license|notice|copyright|\.nuspec$' } | ForEach-Object {
                        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force
                    }
                }
                $RuntimeConfig = Get-Content -LiteralPath (Join-Path $PublishDirectory 'CodexQuotaShare.runtimeconfig.json') -Raw | ConvertFrom-Json
                foreach ($Framework in $RuntimeConfig.runtimeOptions.includedFrameworks) {
                    if ($Framework.name -ne 'Microsoft.NETCore.App') { continue }
                    $RuntimePackage = 'microsoft.netcore.app.runtime.win-x64/' + $Framework.version
                    $RuntimeSource = Join-Path $EnvironmentOverrides.NUGET_PACKAGES $RuntimePackage
                    $RuntimeDestination = Join-Path $LicenseDirectory ($RuntimePackage -replace '/', '-')
                    New-Item -ItemType Directory -Path $RuntimeDestination -Force | Out-Null
                    foreach ($Name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
                        Copy-Item -LiteralPath (Join-Path $RuntimeSource $Name) -Destination $RuntimeDestination -Force
                    }
                }
                $Archive = Join-Path $ArtifactDirectory 'CodexQuotaShare-win-x64.zip'
                Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $Archive -Force
                $Hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
                "$Hash  CodexQuotaShare-win-x64.zip" | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'SHA256SUMS.txt') -Encoding Ascii
                Write-Output "Portable archive: $Archive"
            }
            'Smoke' {
                $Exe = Join-Path $PublishDirectory 'CodexQuotaShare.exe'
                if (-not (Test-Path -LiteralPath $Exe)) { throw 'Run -Action Publish first.' }
                $Result = Join-Path $ArtifactDirectory 'smoke-result.json'
                if (Test-Path -LiteralPath $Result) { Remove-Item -LiteralPath $Result }
                $StartInfo = New-Object System.Diagnostics.ProcessStartInfo
                $StartInfo.FileName = $Exe
                $StartInfo.Arguments = '--smoke-test "' + $Result + '"'
                $StartInfo.UseShellExecute = $false
                $Process = [System.Diagnostics.Process]::Start($StartInfo)
                if (-not $Process.WaitForExit(30000)) { throw 'GUI smoke test timed out; the process was left running for diagnosis.' }
                if ($Process.ExitCode -ne 0) { throw "GUI failed: exit $($Process.ExitCode)" }
                $Report = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json
                if (-not $Report.passed) { throw 'GUI smoke test failed.' }
                $Report
            }
        }
    } finally { Pop-Location }
} finally {
    foreach ($Entry in $PreviousEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($Entry.Key, $Entry.Value, 'Process') }
}
