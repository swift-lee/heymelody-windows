#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Version = $env:REQUESTED_VERSION,
    [string]$FilamentRuntimeRoot,
    [string]$OutputRoot,
    [string]$RecordRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $RecordRoot) { $RecordRoot = Join-Path $repo 'artifacts/macos-ci' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release/macos' }
if (-not $FilamentRuntimeRoot) { $FilamentRuntimeRoot = Join-Path $repo 'artifacts/filament-compat' }
$RecordRoot = [IO.Path]::GetFullPath($RecordRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$FilamentRuntimeRoot = [IO.Path]::GetFullPath($FilamentRuntimeRoot)
New-Item -ItemType Directory -Path $RecordRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$started = [DateTime]::UtcNow
$sourceSha = $null
$success = $false
$failure = $null

function Invoke-CiProcess {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 300)
    Write-Host "Running $Name; complete output is captured in $RecordRoot."
    $result = Invoke-QuietProcess -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $repo `
        -OutputPath (Join-Path $RecordRoot "$Name.stdout.log") -ErrorPath (Join-Path $RecordRoot "$Name.stderr.log") `
        -TimeoutSeconds $TimeoutSeconds
    $results.Add([pscustomobject]@{ name = $Name; process = $result })
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RecordRoot "$Name-process.json") -Encoding utf8NoBOM
    if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CaptureSucceeded) {
        throw "$Name failed: exit=$($result.ExitCode), timeout=$($result.TimedOut), captured=$($result.CaptureSucceeded)."
    }
    return $result
}

function Read-CiOutput {
    param([string]$Name)
    return [IO.File]::ReadAllText((Join-Path $RecordRoot "$Name.stdout.log")).Trim()
}

try {
    if (-not $IsMacOS -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64' `
        -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'Arm64') {
        throw 'The macOS package job requires native Apple silicon macOS and PowerShell.'
    }
    Import-Module (Join-Path $repo 'tools/QuietProcess.psm1') -Force
    Import-Module (Join-Path $repo 'tools/filament/FilamentArtifactCache.psm1') -Force
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Build-MacOS.ps1'), [ref]$null, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Build-MacOS.ps1 could not be parsed for its default version.' }
        $parameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Version' }
        if (-not $parameter -or -not $parameter.DefaultValue) { throw 'Build-MacOS.ps1 has no default version.' }
        $Version = [string]$parameter.DefaultValue.SafeGetValue()
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid package version: $Version" }
    $policy = Get-FilamentArtifactPolicy -EventName $env:GITHUB_EVENT_NAME -GitRef $env:GITHUB_REF
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $python = (Get-Command python3 -ErrorAction Stop).Source
    $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
    $git = (Get-Command git -ErrorAction Stop).Source
    Invoke-CiProcess source $git @('rev-parse', 'HEAD') | Out-Null
    $sourceSha = Read-CiOutput source
    if ($sourceSha -notmatch '^[a-f0-9]{40}$' -or ($env:GITHUB_SHA -and $sourceSha -cne $env:GITHUB_SHA)) {
        throw 'The checkout does not match the workflow source SHA.'
    }
    $outputs = [ordered]@{
        version = $Version
        commit = $sourceSha.Substring(0, 7)
        'package-publish-eligible' = $policy.PackagePublishEligible.ToString().ToLowerInvariant()
        'package-retention-days' = $policy.PackageRetentionDays
        'receipt-retention-days' = $policy.ReceiptRetentionDays
    }
    foreach ($entry in $outputs.GetEnumerator()) {
        if ($env:GITHUB_OUTPUT) { "$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8NoBOM }
    }
    Invoke-CiProcess dotnet-version $dotnet @('--version') | Out-Null
    if ((Read-CiOutput dotnet-version) -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
    Invoke-CiProcess dotnet-info $dotnet @('--info') | Out-Null
    if ((Read-CiOutput dotnet-info) -notmatch '(?im)^\s*Architecture:\s+arm64\s*$') {
        throw 'The selected .NET host must run as arm64.'
    }
    Invoke-CiProcess python-version $python @('--version') | Out-Null
    Invoke-CiProcess macos-version (Get-Command sw_vers -ErrorAction Stop).Source @() | Out-Null
    Invoke-CiProcess xcode-version (Get-Command xcodebuild -ErrorAction Stop).Source @('-version') | Out-Null
    Invoke-CiProcess sdk-version (Get-Command xcrun -ErrorAction Stop).Source @('--show-sdk-version') | Out-Null
    Invoke-CiProcess clang-version (Get-Command xcrun -ErrorAction Stop).Source @('clang++', '--version') | Out-Null
    [ordered]@{
        schemaVersion = 1; sourceSha = $sourceSha; runtimeIdentifier = 'osx-arm64'; version = $Version
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        macOS = Read-CiOutput macos-version; xcode = Read-CiOutput xcode-version
        sdk = Read-CiOutput sdk-version; clang = Read-CiOutput clang-version
        dotnet = Read-CiOutput dotnet-info; python = Read-CiOutput python-version
        filamentRuntimeRoot = $FilamentRuntimeRoot; outputRoot = $OutputRoot
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RecordRoot 'environment.json') -Encoding utf8NoBOM

    # A missing or invalid same-run artifact is fatal: never enter Build-MacOS's native-build fallback.
    $nativeDirectory = Join-Path $FilamentRuntimeRoot 'runtimes/osx-arm64/native'
    if (-not (Test-Path -LiteralPath (Join-Path $nativeDirectory 'filament-runtime.json') -PathType Leaf)) {
        throw 'The same-run osx-arm64 Filament artifact is missing.'
    }
    $native = & (Join-Path $repo 'tools/Verify-FilamentRuntime.ps1') -Directory $nativeDirectory -RuntimeIdentifier osx-arm64 -RequireCurrent
    $native | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RecordRoot 'native-input.json') -Encoding utf8NoBOM

    foreach ($name in @('Platform', 'MacOS')) {
        Invoke-CiProcess "test-$($name.ToLowerInvariant())" $dotnet @('run', '--project', (Join-Path $repo "tests/HeyMelody.$name.Tests"),
            '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:HeyMelodyDesktopPlatform=macOS') -TimeoutSeconds 900 | Out-Null
    }
    Invoke-CiProcess test-package-macos $python @('-m', 'unittest', 'discover', '-s', 'tools/tests', '-p', 'test_package_macos.py') | Out-Null
    Invoke-CiProcess build $pwsh @('-NoProfile', '-File', (Join-Path $repo 'Build-MacOS.ps1'), '-Version', $Version,
        '-DotNetExecutable', $dotnet, '-FilamentRuntimeRoot', $FilamentRuntimeRoot, '-OutputRoot', $OutputRoot,
        '-SkipTests', '-SkipMetalCheck') -TimeoutSeconds 2400 | Out-Null
    $dmg = Join-Path $OutputRoot "HeyMelody-$Version-macos-arm64.dmg"
    $receipt = Join-Path $OutputRoot "HeyMelody-$Version-macos-arm64.json"
    if (-not (Test-Path -LiteralPath $dmg -PathType Leaf) -or -not (Test-Path -LiteralPath $receipt -PathType Leaf)) {
        throw 'The verified DMG or its JSON receipt was not produced.'
    }
    Copy-Item -LiteralPath $receipt -Destination (Join-Path $RecordRoot 'package-verification.json')
    $success = $true
    if ($env:GITHUB_STEP_SUMMARY) {
        @('### macOS arm64 package', '', "- Version: $Version; source: $sourceSha",
            '- Ad-hoc signed DMG; bundle and DMG checks completed. Metal GPU execution was skipped on this hosted runner.',
            "- Package upload allowed: $($policy.PackagePublishEligible); retention: $($policy.PackageRetentionDays) day(s).",
            '- Targeted offline checks and complete logs are retained for 1 day. No Bluetooth hardware validation was performed.') |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8NoBOM
    }
} catch {
    $success = $false
    $failure = [ordered]@{ message = $_.Exception.ToString(); scriptStack = $_.ScriptStackTrace }
    [Console]::Error.WriteLine($_.ToString())
    [Console]::Error.WriteLine($_.ScriptStackTrace)
} finally {
    [ordered]@{
        schemaVersion = 1; success = $success; version = $Version; sourceSha = $sourceSha
        runtimeIdentifier = 'osx-arm64'; metalGpuVerified = $false; bluetoothHardwareVerified = $false
        startedUtc = $started.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o')
        steps = @($results); failure = $failure
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RecordRoot 'ci-summary.json') -Encoding utf8NoBOM
}
if (-not $success) { exit 1 }
$global:LASTEXITCODE = 0
