#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$OutputPath = [IO.Path]::GetFullPath($OutputPath, $repo)
$prefix = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $OutputPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep native ABI fixture receipts below artifacts.' }
$checks = [Collections.Generic.List[object]]::new()
function Assert-Gate([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { Write-Warning "Failed: $Name" }
}
foreach ($builder in @('Build-Client.ps1', 'Build-MacOS.ps1')) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $builder), [ref]$null, [ref]$null)
    $calls = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.Extent.Text -match '^& .*Invoke-NativeAbiCheck\.ps1'
    }, $true))
    Assert-Gate ($calls.Count -eq 2) "$builder checks native inputs and the actual staged package"
    $skipCanBypass = $false
    $finalizeCanBypassPackage = $false
    foreach ($call in $calls) {
        for ($parent = $call.Parent; $null -ne $parent; $parent = $parent.Parent) {
            if ($parent -is [Management.Automation.Language.IfStatementAst]) {
                foreach ($clause in $parent.Clauses) {
                    if ($clause.Item1.Extent.Text -match '\$SkipTests') { $skipCanBypass = $true }
                    if ($call.Extent.Text -match 'package' -and $clause.Item1.Extent.Text -match '\$FinalizeOnly') {
                        $finalizeCanBypassPackage = $true
                    }
                }
            }
        }
    }
    Assert-Gate (-not $skipCanBypass) "$builder SkipTests cannot bypass native ABI validation"
    Assert-Gate (-not $finalizeCanBypassPackage) "$builder FinalizeOnly rechecks the staged native payload"
}
$fixtureRoot = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) ('native-abi-fixture-' + [Guid]::NewGuid().ToString('N'))
$fixtureChecks = & {
    param([string]$RepoRoot, [string]$FixtureRoot)
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/ci/Invoke-NativeAbiCheck.ps1'), [ref]$null, [ref]$null)
    foreach ($name in @('Get-NativeAbiDotNetHost', 'Assert-NativeAbiReport', 'Invoke-NativeAbiCheckCore')) {
        $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        . ([scriptblock]::Create($function.Extent.Text))
    }
    [pscustomobject]@{ Name = 'Host architecture follows the selected dotnet, not an emulated PowerShell'; Passed = ((Get-NativeAbiDotNetHost "Host:`n Architecture: arm64`n" 'win') -ceq 'win-arm64') }
    [pscustomobject]@{ Name = 'Native macOS dotnet architecture is identified explicitly'; Passed = ((Get-NativeAbiDotNetHost "Host:`n Architecture: arm64`n" 'osx') -ceq 'osx-arm64') }
    foreach ($information in @("Architecture: x64`nArchitecture: arm64`n", 'Architecture: x86')) {
        $rejected = $false
        try { Get-NativeAbiDotNetHost $information 'win' | Out-Null } catch { $rejected = $true }
        [pscustomobject]@{ Name = "Ambiguous or unsupported dotnet host architecture is rejected: $($information.Trim())"; Passed = $rejected }
    }
    $native = Join-Path $FixtureRoot 'native'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    $nativePath = Join-Path $native 'fixture.native'
    $manifestPath = Join-Path $native 'filament-runtime.json'
    [IO.File]::WriteAllText($nativePath, 'This fixture is not an executable native library.')
    [IO.File]::WriteAllText($manifestPath, '{"fixture":true}')
    $static = [pscustomobject]@{
        success = $true; library = 'fixture.native'
        sha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $calls = [Collections.Generic.List[string]]::new()
    $scenario = 'valid'
    $expectedRid = 'win-x64'
    function Invoke-QuietProcess {
        param($FilePath, $ArgumentList, $WorkingDirectory, $OutputPath, $ErrorPath, $TimeoutSeconds)
        $calls.Add($scenario)
        if ($ArgumentList.Count -eq 1 -and $ArgumentList[0] -ceq '--info') {
            [IO.File]::WriteAllText($OutputPath, "Host:`n Architecture: arm64`n")
            [IO.File]::WriteAllText($ErrorPath, '')
            return [pscustomobject]@{ ProcessId = 1; ExitCode = 0; TimedOut = $false; CaptureSucceeded = $true }
        }
        [IO.File]::WriteAllText($OutputPath, 'Mocked ABI child process; no dotnet or native library was executed.')
        [IO.File]::WriteAllText($ErrorPath, '')
        $report = [ordered]@{
            schemaVersion = 1; success = $true; nativeRequested = $true; nativeLibraryLoaded = $true
            runtimeIdentifier = $expectedRid; expectedAbiVersion = 2; abiVersion = 2
            nativeLibraryPath = $nativePath; nativeSha256 = $static.sha256
            manifestPath = $manifestPath; manifestSha256 = $static.manifestSha256
            requiredExports = @('hm_get_abi_version', 'hm_set_log_callback', 'hm_create', 'hm_load_model',
                'hm_set_environment', 'hm_configure', 'hm_render', 'hm_try_copy_latest_frame',
                'hm_get_diagnostics', 'hm_get_last_error', 'hm_destroy')
            exportsVerified = 11; gpuProbeExecuted = $false; nativeBluetoothCalls = 0
        }
        switch ($scenario) {
            'wrong-hash' { $report.nativeSha256 = '0' * 64 }
            'wrong-rid' { $report.runtimeIdentifier = 'linux-x64' }
            'wrong-path' { $report.nativeLibraryPath = Join-Path $native 'another.native' }
            'wrong-abi' { $report.abiVersion = 1 }
            'missing-export' { $report.requiredExports = @($report.requiredExports | Where-Object { $_ -ne 'hm_set_log_callback' }) }
            'partial-exports' { $report.exportsVerified = 1 }
            'not-loaded' { $report.nativeLibraryLoaded = $false }
            'gpu-claimed' { $report.gpuProbeExecuted = $true }
            'changed-file' { [IO.File]::AppendAllText($nativePath, 'changed') }
        }
        if ($scenario -ne 'missing-report') {
            $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) 'native-abi-report.json') -Encoding utf8NoBOM
        }
        [pscustomobject]@{
            ProcessId = 1; ExitCode = $(if ($scenario -eq 'process-failed') { 1 } else { 0 })
            TimedOut = ($scenario -eq 'timed-out'); CaptureSucceeded = ($scenario -ne 'capture-failed')
        }
    }
    $valid = Invoke-NativeAbiCheckCore $RepoRoot $native $expectedRid 'win-x64' (Join-Path $FixtureRoot 'valid') 'not-an-executable' $static
    [pscustomobject]@{ Name = 'Same-host x64 requires a successful exact native ABI report'; Passed = ($valid.validation.success -and $valid.validation.nativeAbiVerified -and $valid.validation.executed) }
    $expectedRid = 'win-arm64'
    $arm = Invoke-NativeAbiCheckCore $RepoRoot $native $expectedRid 'win-arm64' (Join-Path $FixtureRoot 'native-arm') 'not-an-executable' $static
    [pscustomobject]@{ Name = 'A native Windows ARM64 host really runs the ABI gate'; Passed = ($arm.validation.nativeAbiVerified -and $calls.Count -eq 2) }
    $auto = Invoke-NativeAbiCheckCore $RepoRoot $native $expectedRid $null (Join-Path $FixtureRoot 'auto-host') 'not-an-executable' $static 'win'
    [pscustomobject]@{ Name = 'Production host discovery runs metadata then the matching ARM64 ABI check'; Passed = ($auto.validation.nativeAbiVerified -and $auto.validation.hostRuntimeIdentifier -ceq 'win-arm64' -and $null -ne $auto.validation.hostProcess -and $calls.Count -eq 4) }
    $cross = Invoke-NativeAbiCheckCore $RepoRoot $native $expectedRid 'win-x64' (Join-Path $FixtureRoot 'cross-arm') 'not-an-executable' $static
    [pscustomobject]@{ Name = 'Cross-compiled ARM64 records explicit static-only scope'; Passed = ($cross.validation.success -and -not $cross.validation.nativeAbiVerified -and -not $cross.validation.nativeLibraryLoaded -and -not $cross.validation.executed -and $cross.validation.skipReason) }
    [pscustomobject]@{ Name = 'Cross-compiled ARM64 never starts a native ABI child'; Passed = ($calls.Count -eq 4) }
    $rejected = $false
    try { Invoke-NativeAbiCheckCore $RepoRoot $native 'osx-arm64' 'osx-x64' (Join-Path $FixtureRoot 'wrong-mac-host') 'not-an-executable' $static | Out-Null }
    catch { $rejected = $true }
    [pscustomobject]@{ Name = 'macOS packaging cannot silently downgrade an incompatible host to static-only'; Passed = $rejected }
    $rejected = $false
    try { Invoke-NativeAbiCheckCore $RepoRoot $native 'win-x64' 'win-x64' (Join-Path $FixtureRoot 'valid') 'not-an-executable' $static | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*new native ABI output*' }
    [pscustomobject]@{ Name = 'An existing native ABI output directory cannot supply stale success'; Passed = $rejected }
    $expectedRid = 'win-x64'
    foreach ($scenario in @('wrong-hash', 'wrong-rid', 'wrong-path', 'wrong-abi', 'missing-export', 'partial-exports',
        'not-loaded', 'gpu-claimed', 'missing-report', 'process-failed', 'timed-out', 'capture-failed', 'changed-file')) {
        $output = Join-Path $FixtureRoot $scenario
        $rejected = $false
        try { Invoke-NativeAbiCheckCore $RepoRoot $native $expectedRid 'win-x64' $output 'not-an-executable' $static | Out-Null }
        catch { $rejected = $true }
        $receipt = Get-Content -LiteralPath (Join-Path $output 'native-abi-validation.json') -Raw | ConvertFrom-Json
        [pscustomobject]@{ Name = "ABI gate rejects $scenario and retains a failed receipt"; Passed = ($rejected -and -not $receipt.success -and $receipt.error) }
    }
} $repo $fixtureRoot
foreach ($check in $fixtureChecks) { Assert-Gate ([bool]$check.Passed) $check.Name }
$failed = @($checks | Where-Object { -not $_.passed }).Count
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
[ordered]@{ schemaVersion = 1; success = ($failed -eq 0); passed = ($checks.Count - $failed); failed = $failed; checks = $checks.ToArray(); nativeCallsExecuted = $false } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host "Native ABI gate fixtures: $($checks.Count - $failed) passed, $failed failed. Receipt: $OutputPath"
if ($failed) { exit 1 }
$global:LASTEXITCODE = 0
