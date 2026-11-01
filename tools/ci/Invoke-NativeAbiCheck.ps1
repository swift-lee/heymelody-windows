#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'osx-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$DotNetExecutable
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Get-NativeAbiDotNetHost {
    param([string]$Information, [string]$HostOperatingSystem)
    $architectures = @([regex]::Matches($Information, '(?im)^\s*Architecture:\s*(x64|arm64)\s*$') |
        ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Select-Object -Unique)
    if ($architectures.Count -ne 1 -or $HostOperatingSystem -notin @('win', 'osx', 'linux')) {
        throw 'The selected .NET host must report one supported native architecture.'
    }
    return "$HostOperatingSystem-$($architectures[0])"
}

function Assert-NativeAbiReport {
    param([object]$Report, [string]$RuntimeIdentifier, [string]$NativePath, [string]$ManifestPath,
        [string]$NativeSha256, [string]$ManifestSha256)
    $required = @('hm_get_abi_version', 'hm_set_log_callback', 'hm_create', 'hm_load_model',
        'hm_set_environment', 'hm_configure', 'hm_render', 'hm_try_copy_latest_frame',
        'hm_get_diagnostics', 'hm_get_last_error', 'hm_destroy')
    if ($Report.schemaVersion -ne 1 -or -not $Report.success -or -not $Report.nativeRequested -or
        -not $Report.nativeLibraryLoaded -or $Report.runtimeIdentifier -cne $RuntimeIdentifier -or
        $Report.expectedAbiVersion -ne 2 -or $Report.abiVersion -ne 2 -or
        $Report.exportsVerified -ne $required.Count -or
        ((@($Report.requiredExports | Sort-Object) -join "`n") -cne (@($required | Sort-Object) -join "`n")) -or
        $Report.nativeSha256 -cne $NativeSha256 -or $Report.manifestSha256 -cne $ManifestSha256 -or
        $Report.gpuProbeExecuted -or $Report.nativeBluetoothCalls -ne 0) {
        throw 'The exact native ABI report did not verify this runtime, hashes, ABI 2 and all required exports.'
    }
    $comparison = if ($RuntimeIdentifier.StartsWith('win-')) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [IO.Path]::GetFullPath($Report.nativeLibraryPath).Equals($NativePath, $comparison) -or
        -not [IO.Path]::GetFullPath($Report.manifestPath).Equals($ManifestPath, $comparison)) {
        throw 'The native ABI report refers to different input paths.'
    }
}

function Invoke-NativeAbiCheckCore {
    param([string]$Repo, [string]$NativeDirectory, [string]$RuntimeIdentifier, [string]$HostRuntimeIdentifier,
        [string]$OutputRoot, [string]$DotNetExecutable, [object]$StaticVerification, [string]$HostOperatingSystem)
    if (Test-Path -LiteralPath $OutputRoot) { throw "Use a new native ABI output directory: $OutputRoot" }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $nativePath = [IO.Path]::GetFullPath((Join-Path $NativeDirectory $StaticVerification.library))
    $manifestPath = [IO.Path]::GetFullPath((Join-Path $NativeDirectory 'filament-runtime.json'))
    $receiptPath = Join-Path $OutputRoot 'native-abi-validation.json'
    $receipt = [ordered]@{
        schemaVersion = 1; success = $false; runtimeIdentifier = $RuntimeIdentifier
        hostRuntimeIdentifier = $HostRuntimeIdentifier; nativeRequested = $false; executed = $false
        nativeAbiVerified = $false; nativeLibraryLoaded = $false; expectedAbiVersion = 2
        nativeLibraryPath = $nativePath; nativeSha256 = $StaticVerification.sha256
        manifestPath = $manifestPath; manifestSha256 = $StaticVerification.manifestSha256
        staticVerification = $StaticVerification; nativeReportPath = $null; process = $null
        hostProcess = $null
        gpuProbeExecuted = $false; nativeBluetoothCalls = 0; skipReason = $null; error = $null
        scope = 'Exact native ABI load and required exports only; no GPU, UI or Bluetooth hardware validation.'
    }
    try {
        if (-not $HostRuntimeIdentifier) {
            $language = [Environment]::GetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE')
            try {
                $env:DOTNET_CLI_UI_LANGUAGE = 'en'
                $hostOutput = Join-Path $OutputRoot 'dotnet-info.stdout.log'
                $hostProcess = Invoke-QuietProcess -FilePath $DotNetExecutable -ArgumentList @('--info') -WorkingDirectory $Repo `
                    -OutputPath $hostOutput -ErrorPath (Join-Path $OutputRoot 'dotnet-info.stderr.log') -TimeoutSeconds 60
            } finally { [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', $language, 'Process') }
            $receipt.hostProcess = $hostProcess
            if ($hostProcess.ExitCode -ne 0 -or $hostProcess.TimedOut -or -not $hostProcess.CaptureSucceeded) {
                throw 'The selected .NET host architecture could not be determined.'
            }
            $HostRuntimeIdentifier = Get-NativeAbiDotNetHost ([IO.File]::ReadAllText($hostOutput)) $HostOperatingSystem
            $receipt.hostRuntimeIdentifier = $HostRuntimeIdentifier
        }
        if ($HostRuntimeIdentifier -cne $RuntimeIdentifier) {
            if (-not $HostRuntimeIdentifier.StartsWith('win-') -or -not $RuntimeIdentifier.StartsWith('win-')) {
                throw 'The requested native ABI check requires a matching native operating system and architecture.'
            }
            $receipt.skipReason = 'Cross-architecture Windows publication: the target library cannot load in this host process.'
            $receipt.scope = 'Static native verification only; target architecture was not loaded or executed.'
        } else {
            $receipt.nativeRequested = $true
            $arguments = @('run', '--project', (Join-Path $Repo 'tests/HeyMelody.Rendering.Filament.Tests'),
                '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false',
                ('-p:RestorePackagesPath=' + (Join-Path $Repo 'artifacts/nuget-cache')),
                ('-p:HeyMelodyDesktopPlatform=' + $(if ($RuntimeIdentifier.StartsWith('win-')) { 'Windows' } else { 'macOS' })),
                '--', '--native-abi', $NativeDirectory, '--output-dir', $OutputRoot)
            $process = Invoke-QuietProcess -FilePath $DotNetExecutable -ArgumentList $arguments -WorkingDirectory $Repo `
                -OutputPath (Join-Path $OutputRoot 'native-abi.stdout.log') `
                -ErrorPath (Join-Path $OutputRoot 'native-abi.stderr.log') -TimeoutSeconds 900
            $receipt.process = $process
            $receipt.executed = $null -ne $process.ProcessId
            if ($process.ExitCode -ne 0 -or $process.TimedOut -or -not $process.CaptureSucceeded) {
                throw "Native ABI process failed: exit=$($process.ExitCode), timeout=$($process.TimedOut), captured=$($process.CaptureSucceeded)."
            }
            $reportPath = Join-Path $OutputRoot 'native-abi-report.json'
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            Assert-NativeAbiReport $report $RuntimeIdentifier $nativePath $manifestPath `
                $StaticVerification.sha256 $StaticVerification.manifestSha256
            $receipt.nativeReportPath = $reportPath
            $receipt.nativeLibraryLoaded = $true
            $receipt.nativeAbiVerified = $true
        }
        if ((Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $StaticVerification.sha256 -or
            (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $StaticVerification.manifestSha256) {
            throw 'The exact native library or manifest changed after static verification.'
        }
        $receipt.success = $true
    } catch {
        $receipt.error = $_.Exception.ToString()
        throw
    } finally {
        $receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    }
    return [pscustomobject]@{ receiptPath = $receiptPath; validation = $receipt }
}

$NativeDirectory = [IO.Path]::GetFullPath($NativeDirectory)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot, $repo)
$prefix = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native ABI logs must remain below this repository artifacts directory.'
}
if (-not $DotNetExecutable) { $DotNetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
$DotNetExecutable = (Get-Command $DotNetExecutable -ErrorAction Stop).Source
Import-Module (Join-Path $repo 'tools/QuietProcess.psm1') -Force
$static = & (Join-Path $repo 'tools/Verify-FilamentRuntime.ps1') -Directory $NativeDirectory `
    -RuntimeIdentifier $RuntimeIdentifier -RequireCurrent
$hostOperatingSystem = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
Invoke-NativeAbiCheckCore $repo $NativeDirectory $RuntimeIdentifier $null $OutputRoot $DotNetExecutable $static $hostOperatingSystem
$global:LASTEXITCODE = 0
