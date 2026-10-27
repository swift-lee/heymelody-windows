#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.9.2',
    [string]$DotNetExecutable,
    [string]$FilamentRuntimeRoot,
    [string]$OutputRoot,
    [string]$BundlePath,
    [string]$ValidationPath,
    [string]$Proxy,
    [ValidateRange(1,64)][int]$Parallel = 2,
    [switch]$SkipTests,
    [switch]$SkipRuntimeChecks,
    [switch]$SkipMetalCheck,
    [switch]$TestsOnly,
    [switch]$StageOnly,
    [switch]$FinalizeOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsMacOS -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') {
    throw 'Build the macOS package on an Apple silicon Mac. Intel macOS is not a supported application target.'
}
if ($FinalizeOnly -and (-not $BundlePath -or $StageOnly)) { throw 'FinalizeOnly requires BundlePath and cannot be combined with StageOnly.' }
if ($TestsOnly -and ($SkipTests -or $StageOnly -or $FinalizeOnly -or $BundlePath)) { throw 'TestsOnly cannot be combined with SkipTests, StageOnly, FinalizeOnly or BundlePath.' }
if (-not $FinalizeOnly -and $BundlePath) { throw 'BundlePath is only an input to FinalizeOnly; ordinary builds always use a fresh staging directory.' }
$repo = $PSScriptRoot
if (-not $DotNetExecutable) {
    $localSdk = Join-Path $repo 'artifacts/tools/dotnet/dotnet'
    $DotNetExecutable = if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotNetExecutable = (Get-Command $DotNetExecutable -ErrorAction Stop).Source
if (-not $FilamentRuntimeRoot) { $FilamentRuntimeRoot = Join-Path $repo 'artifacts/filament' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release/macos' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$FilamentRuntimeRoot = [IO.Path]::GetFullPath($FilamentRuntimeRoot)
$python = (Get-Command python3 -ErrorAction Stop).Source
$packager = Join-Path $repo 'tools/package_macos.py'
$savedEnvironment = [Environment]::GetEnvironmentVariables()
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetExecutable
    $env:DOTNET_CLI_HOME = Join-Path $repo 'artifacts/tools/dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:PYTHONPYCACHEPREFIX = Join-Path $repo 'artifacts/python-pycache'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $toolchain = Get-Content -LiteralPath (Join-Path $repo 'packaging/macos/toolchain.lock.json') -Raw | ConvertFrom-Json
    $localBuildTools = @(@(
        (Join-Path $repo "artifacts/tools/cmake-$($toolchain.cmake.version)-macos-universal/CMake.app/Contents/bin"),
        (Join-Path $repo "artifacts/tools/ninja-$($toolchain.ninja.version)")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    if ($localBuildTools.Count) { $env:PATH = ($localBuildTools -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $env:PATH }
    if ($Proxy) { $env:HTTP_PROXY = $Proxy; $env:HTTPS_PROXY = $Proxy }
    $sdk = & $DotNetExecutable --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
    $tests = @()
    $common = @(('-p:RestorePackagesPath=' + (Join-Path $repo 'artifacts/nuget-cache')),
        '-p:CopyDebugSymbolFilesFromPackages=false', '-p:HeyMelodyDesktopPlatform=macOS',
        ('-p:FilamentRuntimeRoot=' + $FilamentRuntimeRoot),
        '-p:UseSharedCompilation=false')
    if (-not $FinalizeOnly) {
        $work = Join-Path $repo ('artifacts/macos/build-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        if (-not $SkipTests) {
            $originalTestTemporaryDirectory = [Environment]::GetEnvironmentVariable('TMPDIR')
            try {
                # Legacy isolated pipe fixtures use .NET's relative pipe names, which
                # inherit macOS's long TMPDIR. Production endpoints use absolute paths.
                $env:TMPDIR = '/tmp'
                foreach ($project in @('Platform', 'MacOS', 'Diagnostics', 'Protocol', 'Resources', 'Devices', 'Control', 'Daily', 'Calibration',
                    'Client', 'Host', 'App', 'DeviceManager', 'Calibration.UI', 'Rendering.Filament', 'ModelPreview')) {
                    $name = 'HeyMelody.' + $project + '.Tests'
                    $testLog = Join-Path $work ($name + '.log')
                    & $DotNetExecutable build (Join-Path $repo "tests/$name/$name.csproj") -c Release -m:1 -nr:false @common *> $testLog
                    $testExit = $LASTEXITCODE
                    if ($testExit -eq 0) {
                        & $DotNetExecutable (Join-Path $repo "tests/$name/bin/Release/net10.0/$name.dll") *>> $testLog
                        $testExit = $LASTEXITCODE
                    }
                    $testLines = @(Get-Content -LiteralPath $testLog)
                    $skipReasons = @($testLines | Where-Object { $_ -match '^SKIP ' })
                    $skipped = 0
                    foreach ($reason in $skipReasons) {
                        $skipped += if ($reason -match '^SKIP (\d+) ') { [int]$Matches[1] } else { 1 }
                    }
                    $passed = $null
                    foreach ($line in $testLines) {
                        if ($line -match '^\{.*"passed"\s*:') {
                            try { $passed = [int](($line | ConvertFrom-Json).passed) } catch { }
                        } elseif ($line -match '^PASS (\d+) ' -or $line -match '^(\d+)/\d+ .*tests passed' -or
                            $line -match '^(\d+) .*tests passed\.' -or $line -match '^Daily tests: (\d+) passed' -or
                            $line -match '^Platform checks: (\d+) passed' -or $line -match '^Diagnostics checks passed: (\d+)') {
                            $passed = [int]$Matches[1]
                        }
                    }
                    $summaryLines = @($testLines | Where-Object {
                        $_ -match '^(PASS|SKIP|FAIL|Platform checks:|Daily tests:|\d+[/ ]|\{)' -or
                        $_ -match 'checks passed:|tests passed\.|error [A-Z]+\d+'
                    } | Select-Object -Last 3 | ForEach-Object {
                        $portable = $_.Replace($repo, '.')
                        if ($portable.Length -gt 400) { $portable.Substring(0,400) } else { $portable }
                    })
                    $tests += [ordered]@{ project = $name; exitCode = $testExit; passed = $passed; skipped = $skipped; skipReasons = $skipReasons;
                        log = [IO.Path]::GetRelativePath($repo, $testLog);
                        logSha256 = (Get-FileHash -LiteralPath $testLog -Algorithm SHA256).Hash.ToLowerInvariant();
                        summary = $summaryLines; scope = 'offline or simulated; no Bluetooth hardware' }
                    Write-Host ($name + ': exit ' + $testExit + '; ' + ($summaryLines -join ' | '))
                }
            } finally {
                [Environment]::SetEnvironmentVariable('TMPDIR', $originalTestTemporaryDirectory, 'Process')
            }
            $testReceipt = Join-Path $work 'offline-validation.json'
            $testsSucceeded = @($tests | Where-Object { $_.exitCode -ne 0 }).Count -eq 0
            [ordered]@{ schemaVersion = 1; version = $Version; runtimeIdentifier = 'osx-arm64'; success = $testsSucceeded;
                tests = $tests; testProcessEnvironment = @{ TMPDIR = '/tmp'; reason = 'legacy relative pipe fixtures; production endpoint behavior is tested independently' };
                bluetoothHardwareVerified = $false; nativeWindowsExecutionVerified = $false } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testReceipt -Encoding utf8NoBOM
            if (-not $testsSucceeded) { throw "Offline tests failed; see $testReceipt" }
            if ($TestsOnly) {
                [ordered]@{ validation = $testReceipt; version = $Version; runtimeIdentifier = 'osx-arm64'; success = $true; published = $false } | ConvertTo-Json -Compress
                return
            }
        }
        $nativeDirectory = Join-Path $FilamentRuntimeRoot 'runtimes/osx-arm64/native'
        if (-not (Test-Path -LiteralPath (Join-Path $nativeDirectory 'filament-runtime.json'))) {
            & (Join-Path $repo 'tools/filament/Build-FilamentNative.ps1') -RuntimeIdentifier osx-arm64 -OutputRoot $FilamentRuntimeRoot -FetchSource -Parallel $Parallel
            if ($LASTEXITCODE -ne 0) { throw 'Filament Metal build failed.' }
        }
        & (Join-Path $repo 'tools/Verify-FilamentRuntime.ps1') -Directory $nativeDirectory -RuntimeIdentifier osx-arm64 -RequireCurrent | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Filament runtime verification failed.' }
        $publishRoot = Join-Path $work 'publish'
        foreach ($component in @('HeyMelody.App', 'HeyMelody.Host', 'HeyMelody.DeviceManager', 'HeyMelody.FitTest', 'HeyMelody.HearingSetup')) {
            & $DotNetExecutable publish (Join-Path $repo "src/$component/$component.csproj") -c Release -r osx-arm64 --self-contained true `
                -o (Join-Path $publishRoot $component) '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false' `
                "-p:Version=$Version" @common --nologo -m:1 -nr:false
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $component" }
        }
        $BundlePath = Join-Path $work 'HeyMelody.app'
        $stageArguments = @($packager, 'stage', '--repo', $repo, '--publish-root', $publishRoot,
            '--bundle', $BundlePath, '--version', $Version, '--filament-runtime', $nativeDirectory)
        if ($SkipRuntimeChecks) { $stageArguments += '--skip-runtime-checks' }
        if ($ValidationPath) { $stageArguments += @('--validation', [IO.Path]::GetFullPath($ValidationPath)) }
        if ($tests.Count) {
            $stageArguments += @('--validation', $testReceipt)
        }
        & $python @stageArguments
        if ($LASTEXITCODE -ne 0) { throw 'macOS bundle assembly or verification failed.' }
    }
    if ($StageOnly) {
        [ordered]@{ bundle = $BundlePath; version = $Version; runtimeIdentifier = 'osx-arm64'; dmgCreated = $false } | ConvertTo-Json -Compress
        return
    }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $dmg = Join-Path $OutputRoot "HeyMelody-${Version}-macos-arm64.dmg"
    $finalArguments = @($packager, 'dmg', '--repo', $repo, '--bundle', [IO.Path]::GetFullPath($BundlePath), '--output', $dmg, '--version', $Version)
    if ($SkipRuntimeChecks) { $finalArguments += '--skip-runtime-checks' }
    if ($SkipMetalCheck) { $finalArguments += '--skip-metal-check' }
    if ($ValidationPath) { $finalArguments += @('--validation', [IO.Path]::GetFullPath($ValidationPath)) }
    & $python @finalArguments
    if ($LASTEXITCODE -ne 0) { throw 'DMG creation or verification failed.' }
} finally {
    foreach ($key in @([Environment]::GetEnvironmentVariables().Keys)) {
        if (-not $savedEnvironment.Contains($key)) { [Environment]::SetEnvironmentVariable([string]$key, $null, 'Process') }
    }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable([string]$key, [string]$savedEnvironment[$key], 'Process') }
}
