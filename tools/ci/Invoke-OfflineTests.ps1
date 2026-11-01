#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Windows', 'MacOS', 'Renderer')][string]$Profile,
    [string]$OutputRoot,
    [string]$DotNetExecutable,
    [string]$PythonExecutable,
    [string]$Version,
    [switch]$ListProjects
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$common = @('Platform', 'Diagnostics', 'Protocol', 'Resources', 'Devices', 'Control', 'Daily', 'Calibration',
    'Client', 'Host', 'App', 'DeviceManager', 'Calibration.UI', 'Rendering.Filament', 'ModelPreview')
$names = switch ($Profile) {
    Windows { $common + @('MacOS', 'InstallerSupport', 'Probe', 'Windows.OfflineChecks') }
    MacOS { $common + @('MacOS') }
    Renderer { @('Rendering.Filament', 'ModelPreview') }
}
$projects = @($names | ForEach-Object {
    $name = "HeyMelody.$_.Tests"
    $path = "tests/$name/$name.csproj"
    if ($_ -eq 'Windows.OfflineChecks') {
        $name = 'HeyMelody.Windows.OfflineChecks'
        $path = "src/HeyMelody.Windows/OfflineChecks/$name.csproj"
    }
    [pscustomobject]@{ name = $name; path = $path }
})
if ($ListProjects) {
    [ordered]@{
        profile = $Profile; projects = $projects; pythonStructureTests = $true
        pythonPackageTests = ($Profile -eq 'MacOS'); pythonReleaseVerifierTests = ($Profile -eq 'Windows')
    } |
        ConvertTo-Json -Depth 4 -Compress
    return
}
if (($Profile -eq 'Windows' -and -not $IsWindows) -or ($Profile -eq 'MacOS' -and -not $IsMacOS)) {
    throw "Run the $Profile profile on its native operating system; use ListProjects for inspection."
}
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repo ("artifacts/offline-tests/$Profile-" + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot, $repo)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Offline test output must remain below this repository artifacts directory.'
}
if (Test-Path -LiteralPath $OutputRoot) { throw "Use a new offline test output directory: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Import-Module (Join-Path $repo 'tools/QuietProcess.psm1') -Force
$results = [Collections.Generic.List[object]]::new()
$started = [DateTime]::UtcNow
$success = $false
$checksSucceeded = $true
$failure = $null
$originalTmpDir = [Environment]::GetEnvironmentVariable('TMPDIR')
$desktopPlatform = if ($IsWindows) { 'Windows' } elseif ($IsMacOS) { 'macOS' } else { 'Linux' }
$runtimeIdentifier = $(if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }) +
    '-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()

function Invoke-OfflineCheck {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 900)
    Write-Host "Offline check: $Name"
    $stdout = Join-Path $OutputRoot "$Name.stdout.log"
    $stderr = Join-Path $OutputRoot "$Name.stderr.log"
    $process = $null
    try {
        $process = Invoke-QuietProcess -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $repo `
            -OutputPath $stdout -ErrorPath $stderr -TimeoutSeconds $TimeoutSeconds
        $passed = $null
        $skipped = 0
        $skipReasons = [Collections.Generic.List[string]]::new()
        $reader = [IO.StreamReader]::new($stdout)
        try {
            while (($line = $reader.ReadLine()) -ne $null) {
                if ($line -match '^SKIP ') {
                    $skipped += $(if ($line -match '^SKIP (\d+) ') { [int]$Matches[1] } else { 1 })
                    if ($skipReasons.Count -lt 40) { $skipReasons.Add($line.Substring(0, [Math]::Min(800, $line.Length))) }
                }
                if ($line -match '"passed"\s*:\s*(\d+)' -or $line -match '^PASS (\d+) ' -or
                    $line -match '^(\d+)/\d+ .*tests passed' -or $line -match '^(\d+) .*tests passed\.' -or
                    $line -match '^Daily tests: (\d+) passed' -or $line -match '^Platform checks: (\d+) passed' -or
                    $line -match '^Diagnostics checks passed: (\d+)') {
                    $passed = [int]$Matches[1]
                }
            }
        } finally { $reader.Dispose() }
        $checkSucceeded = $process.ExitCode -eq 0 -and -not $process.TimedOut -and $process.CaptureSucceeded
        $results.Add([ordered]@{
            project = $Name; exitCode = $process.ExitCode; passed = $passed; skipped = $skipped
            success = $checkSucceeded; executed = ($null -ne $process.ProcessId)
            skipReasons = @($skipReasons); process = $process
            stdoutSha256 = (Get-FileHash -LiteralPath $stdout -Algorithm SHA256).Hash.ToLowerInvariant()
            stderrSha256 = (Get-FileHash -LiteralPath $stderr -Algorithm SHA256).Hash.ToLowerInvariant()
            scope = 'offline or simulated; no Bluetooth hardware or native GPU validation'
        })
        if (-not $checkSucceeded) {
            Write-Warning "$Name failed: exit=$($process.ExitCode), timeout=$($process.TimedOut), captured=$($process.CaptureSucceeded)."
            return $false
        }
        Write-Host "$Name passed; reported checks=$passed, skipped=$skipped."
        return $true
    } catch {
        $results.Add([ordered]@{
            project = $Name; success = $false; executed = ($null -ne $process -and $null -ne $process.ProcessId)
            process = $process; error = $_.Exception.ToString()
        })
        Write-Warning "$Name could not complete: $($_.Exception.Message)"
        return $false
    }
}

try {
    if (-not $DotNetExecutable) { $DotNetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
    $DotNetExecutable = (Get-Command $DotNetExecutable -ErrorAction Stop).Source
    $properties = @('-p:CopyDebugSymbolFilesFromPackages=false', '-p:UseSharedCompilation=false',
        "-p:HeyMelodyDesktopPlatform=$desktopPlatform", ('-p:RestorePackagesPath=' + (Join-Path $repo 'artifacts/nuget-cache')))
    $fallback = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    if (Test-Path -LiteralPath $fallback -PathType Container) { $properties += '-p:RestoreFallbackFolders=' + $fallback }
    # Only legacy relative-pipe fixtures need this; production endpoints retain their own path policy.
    if ($IsMacOS) { $env:TMPDIR = '/tmp' }
    foreach ($project in $projects) {
        $arguments = @('run', '--project', (Join-Path $repo $project.path), '-c', 'Release', '--disable-build-servers') + $properties
        if ($project.name -eq 'HeyMelody.ModelPreview.Tests') {
            $arguments += @('--', '--output-dir', (Join-Path $OutputRoot 'model-preview'))
        }
        if (-not (Invoke-OfflineCheck $project.name $DotNetExecutable $arguments)) { $checksSucceeded = $false }
    }
    if (-not $PythonExecutable) {
        $PythonExecutable = (Get-Command $(if ($IsWindows) { 'python' } else { 'python3' }) -ErrorAction Stop).Source
    }
    $PythonExecutable = (Get-Command $PythonExecutable -ErrorAction Stop).Source
    if (-not (Invoke-OfflineCheck 'python-project-structure' $PythonExecutable @('-m', 'unittest', 'discover', '-s', 'tools/tests', '-p', 'test_project_structure.py') 300)) {
        $checksSucceeded = $false
    }
    if ($Profile -eq 'MacOS') {
        if (-not (Invoke-OfflineCheck 'python-package-macos' $PythonExecutable @('-m', 'unittest', 'discover', '-s', 'tools/tests', '-p', 'test_package_macos.py') 300)) {
            $checksSucceeded = $false
        }
    }
    if ($Profile -eq 'Windows') {
        if (-not (Invoke-OfflineCheck 'python-release-verifier' $PythonExecutable @('-m', 'unittest', 'discover', '-s', 'tools/tests', '-p', 'test_verify_release.py') 300)) {
            $checksSucceeded = $false
        }
    }
    if (-not $checksSucceeded) { throw 'One or more offline checks failed; see offline-validation.json and the per-runner logs.' }
    $success = $true
} catch {
    $failure = $_.Exception.ToString()
    throw
} finally {
    [Environment]::SetEnvironmentVariable('TMPDIR', $originalTmpDir, 'Process')
    [ordered]@{
        schemaVersion = 1; success = $success; profile = $Profile; version = $Version; runtimeIdentifier = $runtimeIdentifier
        startedUtc = $started.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o')
        plannedProjects = @($projects | ForEach-Object name)
        plannedPythonChecks = @('python-project-structure') +
            $(if ($Profile -eq 'MacOS') { @('python-package-macos') } else { @() }) +
            $(if ($Profile -eq 'Windows') { @('python-release-verifier') } else { @() })
        tests = $results.ToArray(); failure = $failure; bluetoothHardwareVerified = $false; nativeGpuVerified = $false
        testProcessEnvironment = @{ TMPDIR = $(if ($IsMacOS) { '/tmp' } else { $originalTmpDir }) }
    } | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $OutputRoot 'offline-validation.json') -Encoding utf8NoBOM
}
[ordered]@{ success = $true; profile = $Profile; validation = (Join-Path $OutputRoot 'offline-validation.json') } |
    ConvertTo-Json -Compress
$global:LASTEXITCODE = 0
