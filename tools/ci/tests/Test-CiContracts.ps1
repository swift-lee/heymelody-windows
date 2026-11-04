#Requires -Version 7.4
[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repo 'artifacts/ci-contract-checks/summary.json' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath, $repo)
$prefix = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $OutputPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep CI check receipts below artifacts.' }
Import-Module (Join-Path $repo 'tools/ci/ArtifactPolicy.psm1') -Force
$checks = [Collections.Generic.List[object]]::new()
function Assert-Contract([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { Write-Warning "Failed: $Name" }
}

foreach ($event in @('push', 'workflow_dispatch', 'pull_request', 'pull_request_target', 'schedule', 'workflow_call', '')) {
    foreach ($ref in @('refs/heads/main', 'refs/heads/review/0.9.9', 'refs/tags/v0.9.2', 'refs/heads/main-feature', 'refs/heads/MAIN')) {
        $policy = Get-CiArtifactPolicy -EventName $event -GitRef $ref
        $trusted = $event -in @('push', 'workflow_dispatch')
        Assert-Contract ($policy.CachePublishEligible -eq $trusted -and $policy.PackagePublishEligible -eq $trusted) "$event $ref upload trust"
        Assert-Contract ($policy.PackageRetentionDays -eq $(if ($ref -ceq 'refs/heads/main') { 7 } else { 1 })) "$event $ref exact main retention"
        Assert-Contract ($policy.ReceiptRetentionDays -eq 1 -and $policy.CacheRetentionDays -eq 30) "$event $ref receipt and cache retention"
    }
}
$profiles = @{}
foreach ($profile in @('Windows', 'MacOS', 'Renderer')) {
    $description = & (Join-Path $repo 'tools/ci/Invoke-OfflineTests.ps1') -Profile $profile -ListProjects | ConvertFrom-Json
    $profiles[$profile] = @($description.projects | ForEach-Object name)
    Assert-Contract ($profiles[$profile].Count -eq @($profiles[$profile] | Select-Object -Unique).Count) "$profile project list is unique"
    Assert-Contract ($description.pythonStructureTests -eq $true) "$profile checks project structure"
    Assert-Contract ($description.pythonUiAssetTests -eq $true) "$profile checks UI font and license sources"
    Assert-Contract ($description.pythonPackageTests -eq ($profile -eq 'MacOS')) "$profile packaging fixtures policy"
    Assert-Contract ($description.pythonReleaseVerifierTests -eq ($profile -eq 'Windows')) "$profile Windows release verifier fixtures policy"
    foreach ($project in $description.projects) {
        Assert-Contract (Test-Path -LiteralPath (Join-Path $repo $project.path) -PathType Leaf) "$profile $($project.name) exists"
    }
    Assert-Contract (-not ($profiles[$profile] -contains 'HeyMelody.Audio.Tests')) "$profile excludes optional Audio experiment"
}
foreach ($name in @('Platform', 'MacOS', 'ModelPreview', 'InstallerSupport', 'Probe')) {
    Assert-Contract ($profiles.Windows -contains "HeyMelody.$name.Tests") "Windows includes $name"
}
Assert-Contract ($profiles.Windows -contains 'HeyMelody.Windows.OfflineChecks') 'Windows retains legacy offline checks'
Assert-Contract ($profiles.MacOS.Count -eq 16 -and $profiles.Windows.Count -eq 19 -and $profiles.Renderer.Count -eq 2) 'Profile project counts'
Assert-Contract (-not ($profiles.MacOS -contains 'HeyMelody.Probe.Tests') -and -not ($profiles.MacOS -contains 'HeyMelody.Windows.OfflineChecks')) 'macOS does not run Windows-only checks'

foreach ($script in @('Build-Client.ps1', 'Build-MacOS.ps1')) {
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $script), [ref]$null, [ref]$errors)
    Assert-Contract ($errors.Count -eq 0) "$script parses"
    $parameters = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    foreach ($flag in @('SkipTests', 'TestsOnly', 'FinalizeOnly', 'ValidationPath', 'FilamentRuntimeRoot', 'PythonExecutable')) {
        Assert-Contract ($parameters -contains $flag) "$script preserves $flag"
    }
}
$savedOutput = [Environment]::GetEnvironmentVariable('GITHUB_OUTPUT')
try {
    $fixture = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) ('policy-output-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    $env:GITHUB_OUTPUT = Join-Path $fixture 'github-output.txt'
    & (Join-Path $repo 'tools/ci/Write-ArtifactPolicy.ps1') -EventName pull_request -GitRef refs/heads/main | Out-Null
    $lines = @(Get-Content -LiteralPath $env:GITHUB_OUTPUT)
    Assert-Contract ($lines -contains 'package-publish-eligible=false' -and $lines -contains 'package-retention-days=7' -and $lines -contains 'receipt-retention-days=1') 'Workflow output uses neutral artifact policy'
} finally { [Environment]::SetEnvironmentVariable('GITHUB_OUTPUT', $savedOutput, 'Process') }
$runnerFixture = & {
    param([string]$RepoRoot, [string]$FixtureRoot)
    $repo = $RepoRoot
    $OutputRoot = Join-Path $FixtureRoot 'runner'
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $results = [Collections.Generic.List[object]]::new()
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/ci/Invoke-OfflineTests.ps1'), [ref]$null, [ref]$null)
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-OfflineCheck' }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
    function Invoke-QuietProcess {
        param($FilePath, $ArgumentList, $WorkingDirectory, $OutputPath, $ErrorPath, $TimeoutSeconds)
        [IO.File]::WriteAllText($OutputPath, "SKIP 2 fixture checks`n{`"passed`":3}`n")
        [IO.File]::WriteAllText($ErrorPath, '')
        [pscustomobject]@{
            ProcessId = 1; ExitCode = $(if ($OutputPath.Contains('failed-fixture')) { 1 } else { 0 })
            TimedOut = $false; CaptureSucceeded = $true
        }
    }
    $failedResult = Invoke-OfflineCheck 'failed-fixture' 'not-an-executable' @()
    $passedResult = Invoke-OfflineCheck 'passed-fixture' 'not-an-executable' @()
    [pscustomobject]@{
        FailureRejected = ($failedResult -eq $false -and $results[0].success -eq $false)
        LaterRunnerExecuted = ($passedResult -eq $true -and $results.Count -eq 2 -and $results[1].success -eq $true)
        CountsPreserved = ($results[0].passed -eq 3 -and $results[1].skipped -eq 2)
    }
} $repo $fixture
Assert-Contract $runnerFixture.FailureRejected 'Runner failure remains a failed result even with partial passing checks'
Assert-Contract $runnerFixture.LaterRunnerExecuted 'Failure does not prevent a later runner from being collected'
Assert-Contract $runnerFixture.CountsPreserved 'Runner summaries preserve reported pass and skip counts'
$sdkFixture = & {
    param([string]$RepoRoot, [string]$FixtureRoot)
    $action = [IO.File]::ReadAllText((Join-Path $RepoRoot '.github/actions/setup-build-sdk/action.yml'))
    $match = [regex]::Match($action, '(?ms)^    - name: Select the locked SDK.*?^      run: \|\r?\n(?<body>(?:        [^\r\n]*\r?\n?)+)')
    if (-not $match.Success) { throw 'The locked SDK selection step could not be found.' }
    $body = ($match.Groups['body'].Value -split '\r?\n' | ForEach-Object { $_ -replace '^        ', '' }) -join "`n"
    $selectSdk = [scriptblock]::Create($body)
    $lock = Get-Content -LiteralPath (Join-Path $RepoRoot 'packaging/macos/toolchain.lock.json') -Raw | ConvertFrom-Json
    $savedWorkspace = [Environment]::GetEnvironmentVariable('GITHUB_WORKSPACE')
    $savedVersion = [Environment]::GetEnvironmentVariable('HM_BUILD_SDK_VERSION')
    $reportedVersion = $lock.dotnet.version
    function dotnet {
        if ($args.Count -ne 1 -or $args[0] -cne '--version') { throw 'The SDK fixture may only query the version.' }
        $global:LASTEXITCODE = 0
        return $reportedVersion
    }
    try {
        $env:HM_BUILD_SDK_VERSION = $lock.dotnet.version
        $env:GITHUB_WORKSPACE = Join-Path $FixtureRoot 'sdk-selected'
        New-Item -ItemType Directory -Path $env:GITHUB_WORKSPACE -Force | Out-Null
        & $selectSdk
        $selected = Get-Content -LiteralPath (Join-Path $env:GITHUB_WORKSPACE 'global.json') -Raw | ConvertFrom-Json
        $selectedExactly = $selected.sdk.version -ceq $lock.dotnet.version -and $selected.sdk.rollForward -ceq 'disable' -and $selected.sdk.allowPrerelease -eq $false
        $refusedOverwrite = $false
        try { & $selectSdk } catch { $refusedOverwrite = $_.Exception.Message -like '*Refusing to overwrite*' }
        $unchanged = (Get-Content -LiteralPath (Join-Path $env:GITHUB_WORKSPACE 'global.json') -Raw | ConvertFrom-Json).sdk.version -ceq $lock.dotnet.version
        $env:GITHUB_WORKSPACE = Join-Path $FixtureRoot 'sdk-mismatched'
        New-Item -ItemType Directory -Path $env:GITHUB_WORKSPACE -Force | Out-Null
        $reportedVersion = '0.0.0-fixture'
        $rejectedMismatch = $false
        try { & $selectSdk } catch { $rejectedMismatch = $_.Exception.Message -like '*SDK selection mismatch*' }
        [pscustomobject]@{
            SelectedExactly = $selectedExactly; RefusedOverwrite = ($refusedOverwrite -and $unchanged)
            RejectedMismatch = $rejectedMismatch
        }
    } finally {
        [Environment]::SetEnvironmentVariable('GITHUB_WORKSPACE', $savedWorkspace, 'Process')
        [Environment]::SetEnvironmentVariable('HM_BUILD_SDK_VERSION', $savedVersion, 'Process')
    }
} $repo $fixture
Assert-Contract $sdkFixture.SelectedExactly 'CI selects the SDK from the lock without roll-forward or previews'
Assert-Contract $sdkFixture.RefusedOverwrite 'CI refuses to overwrite an existing workspace SDK policy'
Assert-Contract $sdkFixture.RejectedMismatch 'CI rejects a CLI that still selects another installed SDK'
$abiFixturePath = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) 'native-abi-gate.json'
& (Join-Path $PSScriptRoot 'Test-NativeAbiGate.ps1') -OutputPath $abiFixturePath
if ($LASTEXITCODE) { throw 'Native ABI packaging gate fixtures failed.' }
$abiFixture = Get-Content -LiteralPath $abiFixturePath -Raw | ConvertFrom-Json
foreach ($check in $abiFixture.checks) { Assert-Contract ([bool]$check.passed) $check.name }
$failed = @($checks | Where-Object { -not $_.passed }).Count
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
[ordered]@{ schemaVersion = 1; success = ($failed -eq 0); passed = ($checks.Count - $failed); failed = $failed; checks = $checks.ToArray() } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host "CI contracts: $($checks.Count - $failed) passed, $failed failed. Receipt: $OutputPath"
if ($failed) { exit 1 }
$global:LASTEXITCODE = 0
