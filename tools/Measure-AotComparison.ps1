#Requires -Version 7.5
<#
.SYNOPSIS
Compares the published win-x64 R2R/JIT and experimental Native AOT packages.
.DESCRIPTION
Uses only explicit isolated benchmark modes. No headset, live Bluetooth scan,
online resources, user preferences or production IPC endpoints are used.
"Fresh process" does not mean a flushed OS file cache. UI callback and bitmap-copy
intervals are not DWM displayed FPS. Every raw receipt is preserved.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$AotDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(1, 100)][int]$StartupRuns = 20,
    [ValidateRange(1, 30)][int]$SteadyRuns = 5,
    [ValidateRange(1, 120)][int]$Seconds = 30,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 5,
    [ValidateRange(1, 30)][int]$ModelProcessRuns = 10,
    [ValidateRange(5, 30)][int]$ModelHotRuns = 5,
    [switch]$SkipStartup,
    [switch]$SkipSteady,
    [switch]$SkipModels
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Windows is required.' }
if ($env:DOTNET_ReadyToRun -eq '0' -or $env:COMPlus_ReadyToRun -eq '0') {
    throw 'The comparison baseline requires ReadyToRun to remain enabled.'
}
$output = [IO.Path]::GetFullPath($OutputRoot)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
    throw 'OutputRoot must be new or empty.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null

function Get-Build([string]$Directory, [string]$Variant) {
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $binaryRoot = if (Test-Path -LiteralPath (Join-Path $root 'x64/HeyMelody.exe')) { Join-Path $root 'x64' } else { $root }
    $app = Join-Path $binaryRoot 'HeyMelody.exe'
    $hostPath = Join-Path $binaryRoot 'backend/HeyMelody.Host.exe'
    if (!(Test-Path -LiteralPath $hostPath)) { $hostPath = Join-Path $binaryRoot 'HeyMelody.Host.exe' }
    foreach ($file in @($app, $hostPath)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Published executable missing: $file" } }
    $manifestPath = Join-Path $root 'manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { $manifestPath = Join-Path (Split-Path $root) 'manifest.json' }
    if (!(Test-Path -LiteralPath $manifestPath)) { throw "A verified publication manifest is required: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $expectedMode = if ($Variant -eq 'r2r-jit') { 'ReadyToRun' } else { 'AotExperimental' }
    if ($manifest.compilationMode -ne $expectedMode) { throw "$Variant requires manifest compilationMode=$expectedMode." }
    $appHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant()
    $hostHash = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestBuilds = @($manifest.builds | Where-Object {
        $_.guiSha256 -eq $appHash -and $_.hostSha256 -eq $hostHash
    })
    if ($manifestBuilds.Count -ne 1) { throw "Executable hashes do not identify one build in $manifestPath" }
    $files = @(Get-ChildItem -LiteralPath $binaryRoot -File -Recurse)
    $inventory = foreach ($file in $files | Sort-Object FullName) {
        [pscustomobject]@{
            path = [IO.Path]::GetRelativePath($binaryRoot, $file.FullName)
            bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return [pscustomobject]@{
        variant = $Variant; root = $binaryRoot; app = $app; host = $hostPath
        appSha256 = $appHash; hostSha256 = $hostHash
        publishedBytes = [long](($files | Measure-Object Length -Sum).Sum)
        fileCount = $files.Count; manifest = $manifest; fileInventory = @($inventory)
        manifestPath = $manifestPath; manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$builds = @(Get-Build $BaselineDirectory 'r2r-jit'; Get-Build $AotDirectory 'native-aot')

function Write-NewJson([string]$Path, $Value) {
    if (Test-Path -LiteralPath $Path) { throw "Evidence already exists: $Path" }
    $Value | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Start-Benchmark($Build, [string]$Role, [string]$RunId, [string]$Directory,
    [string]$Scenario, [int]$Duration, [bool]$HostReady = $false) {
    $exe = if ($Role -eq 'host') { $Build.host } else { $Build.app }
    $arguments = @('--performance-benchmark', '--run-id', $RunId, '--output-dir', $Directory,
        '--scenario', $Scenario, '--seconds', "$Duration", '--warmup-seconds', "$WarmupSeconds")
    if ($Scenario -eq 'integrated-static') {
        $arguments += @('--host-executable', $Build.host, '--host-ready', "$HostReady".ToLowerInvariant())
    }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path $exe
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{
        Process = $process; Clock = $clock; Directory = $Directory; Executable = $exe; Role = $Role; RunId = $RunId
    }
}

function Wait-Receipt($Launch, [string]$Name = 'ready.json', [int]$TimeoutSeconds = 60) {
    $path = Join-Path $Launch.Directory $Name
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $path) {
            try {
                $receipt = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                if (!$receipt.success) { throw "Benchmark failed: $($receipt.failure)" }
                if ($receipt.runId -ne $Launch.RunId -or $receipt.process.processId -ne $Launch.Process.Id) {
                    throw "Benchmark receipt does not belong to its launched process: $path"
                }
                return [pscustomobject]@{ receipt = $receipt; externalMilliseconds = $Launch.Clock.Elapsed.TotalMilliseconds }
            }
            catch [System.ArgumentException], [System.FormatException] { }
        }
        if ($Launch.Process.HasExited -and !(Test-Path -LiteralPath $path)) {
            $failurePath = Join-Path $Launch.Directory 'benchmark.json'
            $failure = if (Test-Path -LiteralPath $failurePath) {
                (Get-Content -LiteralPath $failurePath -Raw | ConvertFrom-Json).failure
            } else { 'No failure receipt.' }
            throw "Benchmark exited $($Launch.Process.ExitCode) without $Name at $($Launch.Directory): $failure"
        }
        Start-Sleep -Milliseconds 10
    }
    throw "Benchmark timed out waiting for $path"
}

function Stop-IsolatedHost([string]$Directory) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $Directory 'stop'), '')
}

function Get-Identity($Identity, $Build) {
    if (!$Identity) { throw 'Missing process identity.' }
    $expected = if ($Identity.role -eq 'app') { $Build.app } else { $Build.host }
    if (![string]::Equals([IO.Path]::GetFullPath($Identity.executable), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected benchmark executable: $($Identity.executable)"
    }
    if ($Identity.architecture -ne 'X64') { throw "win-x64 is required; got $($Identity.architecture)" }
    $aot = $Build.variant -eq 'native-aot'
    if ([bool]$Identity.dynamicCodeSupported -eq $aot -or [bool]$Identity.dynamicCodeCompiled -eq $aot) {
        throw "Executable dynamic-code flags do not match $($Build.variant)"
    }
    return [pscustomobject]@{
        processId = $Identity.processId; role = $Identity.role; executable = $Identity.executable
        sha256 = if ($Identity.role -eq 'app') { $Build.appSha256 } else { $Build.hostSha256 }
        runtime = $Identity.runtime; architecture = $Identity.architecture
        dynamicCodeSupported = $Identity.dynamicCodeSupported; dynamicCodeCompiled = $Identity.dynamicCodeCompiled
        startedUtcTicks = $Identity.startedUtcTicks
    }
}

function Get-ExternalSample([object[]]$Identities, [double]$Elapsed) {
    $pids = @($Identities | ForEach-Object { [int]$_.processId })
    $counters = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { [int]$_.IDProcess -in $pids })
    $issues = [Collections.Generic.List[string]]::new()
    $rows = foreach ($identity in $Identities) {
        $process = Get-Process -Id $identity.processId -ErrorAction SilentlyContinue
        if (!$process) { $issues.Add("Missing $($identity.role) PID $($identity.processId)"); continue }
        try {
            if (![string]::Equals($process.Path, $identity.executable, [StringComparison]::OrdinalIgnoreCase) -or
                $process.StartTime.ToUniversalTime().Ticks -ne $identity.startedUtcTicks) {
                $issues.Add("Process identity changed: $($identity.processId)")
                continue
            }
            $counter = $counters | Where-Object { [int]$_.IDProcess -eq $identity.processId } | Select-Object -First 1
            if (!$counter) { $issues.Add("No private-working-set counter for PID $($identity.processId)"); continue }
            [pscustomobject]@{
                role = $identity.role; processId = $identity.processId
                cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                privateWorkingSetBytes = [long]$counter.WorkingSetPrivate
                workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64
            }
        }
        catch [System.InvalidOperationException] { $issues.Add("Process ended during sampling: $($identity.processId)") }
        finally { $process.Dispose() }
    }
    return [pscustomobject]@{ seconds = $Elapsed; complete = $issues.Count -eq 0; issues = $issues.ToArray(); processes = @($rows) }
}

$runs = [Collections.Generic.List[object]]::new()
function Invoke-Run($Build, [string]$Kind, [string]$Scenario, [int]$Index, [int]$Duration) {
    $runId = [Guid]::NewGuid().ToString('N')
    $directory = Join-Path $output "$($Build.variant)/$Kind-$Index"
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $hostLaunch = $null; $appLaunch = $null; $identities = @(); $external = [Collections.Generic.List[object]]::new()
    $warm = $Kind -eq 'integrated-ready'
    $hostDirectory = if ($Scenario -eq 'integrated-static' -and !$warm) {
        Join-Path $directory 'app/host'
    } else { Join-Path $directory 'host' }
    try {
        if ($Scenario -eq 'host' -or $warm) {
            $hostLaunch = Start-Benchmark $Build 'host' $runId $hostDirectory 'host' $Duration
            $hostReadyReceipt = Wait-Receipt $hostLaunch
            $identities += Get-Identity $hostReadyReceipt.receipt.process $Build
        }
        if ($Scenario -ne 'host') {
            $appDirectory = Join-Path $directory 'app'
            $appLaunch = Start-Benchmark $Build 'app' $runId $appDirectory $Scenario $Duration $warm
            $ready = Wait-Receipt $appLaunch
            $identities += Get-Identity $ready.receipt.process $Build
            if ($Scenario -eq 'integrated-static' -and !$warm) {
                $hostDirectory = Join-Path $appDirectory 'host'
                $hostReceipt = Get-Content -LiteralPath (Join-Path $hostDirectory 'ready.json') -Raw | ConvertFrom-Json
                $identities += Get-Identity $hostReceipt.process $Build
            }
        }
        else { $ready = $hostReadyReceipt }
        if ($Scenario -eq 'host' -or $Scenario -eq 'integrated-static') {
            $worker = Get-Content -LiteralPath (Join-Path $hostDirectory 'worker.json') -Raw | ConvertFrom-Json
            $identities += Get-Identity $worker $Build
        }
        $primary = if ($appLaunch) { $appLaunch } else { $hostLaunch }
        if ($Duration -gt 0) {
            # Host idle waits for the actual EnergyPolicy grace, not merely the UI warmup.
            $delay = if ($Scenario -eq 'host') { [Math]::Max(17, $WarmupSeconds) } else { $WarmupSeconds }
            Start-Sleep -Seconds $delay
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt $Duration -and !$primary.Process.HasExited) {
                $sample = Get-ExternalSample $identities $timer.Elapsed.TotalSeconds
                if (!$sample.complete) {
                    if ($primary.Process.HasExited -and (Test-Path -LiteralPath (Join-Path $primary.Directory 'benchmark.json'))) { break }
                    throw "Incomplete process sample: $($sample.issues -join '; ')"
                }
                $external.Add($sample)
                Start-Sleep -Milliseconds 500
            }
            $finished = Wait-Receipt $primary 'benchmark.json' ($Duration + 40)
            $report = $finished.receipt
            if ($Scenario -eq 'host' -and @($report.samples | Where-Object energyMode -ne 'Eco').Count) {
                throw 'Host idle samples were not all in the expected Eco mode.'
            }
        }
        else {
            $report = if ($appLaunch) { (Wait-Receipt $appLaunch 'benchmark.json').receipt } else { $ready.receipt }
        }
        if ($report.diagnosticsEnabled) { throw 'Performance comparison must run with diagnostics disabled.' }
        $row = [pscustomobject]@{
            variant = $Build.variant; kind = $Kind; scenario = $Scenario; sample = $Index; runId = $runId
            externalReadyMilliseconds = $ready.externalMilliseconds
            readyMilliseconds = $report.readyMilliseconds; identities = $identities
            readyBoundary = if ($Scenario -eq 'host') {
                'Complete tray, supervisor and simulated worker readiness; not the earlier IPC subscription boundary.'
            } else { 'Product-window ready callback after the required scenario state.' }
            report = $report; externalSamples = $external.ToArray(); directory = $directory
        }
        Write-NewJson (Join-Path $directory 'controller.json') $row
        $runs.Add($row)
        Write-Output "$($Build.variant) $Kind $Index complete"
    }
    finally {
        $cleanupErrors = [Collections.Generic.List[string]]::new()
        if ($Scenario -in @('host', 'integrated-static')) { Stop-IsolatedHost $hostDirectory }
        # A failed App may have already started its isolated children before it
        # produced any App receipt. Discover those identities from the known run directory.
        foreach ($name in @('ready.json', 'worker.json')) {
            $receiptPath = Join-Path $hostDirectory $name
            if (!(Test-Path -LiteralPath $receiptPath)) { continue }
            try {
                $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
                $identity = if ($name -eq 'ready.json') { $receipt.process } else { $receipt }
                if ($identity -and $identity.processId -notin @($identities.processId)) {
                    $identities += Get-Identity $identity $Build
                }
            } catch { $cleanupErrors.Add($_.Exception.Message) }
        }
        foreach ($launch in @($appLaunch, $hostLaunch)) {
            if (!$launch) { continue }
            try {
                if (!$launch.Process.WaitForExit(15000)) {
                    $launch.Process.Kill($true); $cleanupErrors.Add("Isolated benchmark did not exit: $($launch.Directory)")
                }
                elseif ($launch.Process.ExitCode -ne 0) {
                    $cleanupErrors.Add("Isolated benchmark exited $($launch.Process.ExitCode): $($launch.Directory)")
                }
            } catch { $cleanupErrors.Add($_.Exception.Message) }
            finally { $launch.Process.Dispose() }
        }
        foreach ($identity in $identities | Where-Object role -in @('tray', 'worker')) {
            $process = Get-Process -Id $identity.processId -ErrorAction SilentlyContinue
            if (!$process) { continue }
            try {
                if ($process.HasExited) { continue }
                if (![string]::Equals($process.Path, $identity.executable, [StringComparison]::OrdinalIgnoreCase) -or
                    ($identity.startedUtcTicks -and $process.StartTime.ToUniversalTime().Ticks -ne $identity.startedUtcTicks)) {
                    $cleanupErrors.Add("PID identity changed: $($identity.processId)")
                    continue
                }
                if (!$process.WaitForExit(15000)) {
                    $process.Kill($true); $cleanupErrors.Add("Isolated child did not stop: $($identity.processId)")
                }
            } catch {
                if (!$process.HasExited) { $cleanupErrors.Add($_.Exception.Message) }
            }
            finally { $process.Dispose() }
        }
        if ($cleanupErrors.Count) { throw ($cleanupErrors -join "`n") }
    }
}

# Record file hashes before measuring; hashing a large executable is not startup work.
Write-NewJson (Join-Path $output 'session.json') ([ordered]@{
    schemaVersion = 1; startedUtc = [DateTimeOffset]::UtcNow; os = [Environment]::OSVersion.VersionString
    cpu = (Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    builds = $builds; startupRuns = $StartupRuns; steadyRuns = $SteadyRuns; seconds = $Seconds
    warmupSeconds = $WarmupSeconds; modelProcessRuns = $ModelProcessRuns; modelHotRuns = $ModelHotRuns
    runtimeEnvironment = [ordered]@{
        DOTNET_ReadyToRun = $env:DOTNET_ReadyToRun; COMPlus_ReadyToRun = $env:COMPlus_ReadyToRun
        DOTNET_TieredCompilation = $env:DOTNET_TieredCompilation; DOTNET_TieredPGO = $env:DOTNET_TieredPGO
        COMPlus_TieredCompilation = $env:COMPlus_TieredCompilation; COMPlus_TieredPGO = $env:COMPlus_TieredPGO
        DOTNET_GCConserveMemory = $env:DOTNET_GCConserveMemory; DOTNET_GCGen0MaxBudget = $env:DOTNET_GCGen0MaxBudget
    }
    scope = 'Fresh-process and steady synthetic comparison on this win-x64 machine; no OS file-cache flush, real headset or DWM presented-FPS claim.'
})
# One unrecorded rehearsal of each mode, retained in its own evidence directory.
foreach ($build in $builds) { Invoke-Run $build 'rehearsal' 'ui-static' 0 0 }
$hostStartupPhases = $null
if (!$SkipStartup) {
    $phaseRoot = Join-Path $output 'host-startup-phases'
    & (Join-Path $PSScriptRoot 'Measure-AotHostStartup.ps1') `
        -BaselineDirectory $builds[0].root -AotDirectory $builds[1].root `
        -OutputRoot $phaseRoot -StartupRuns $StartupRuns
    $phaseSummary = Get-Content -LiteralPath (Join-Path $phaseRoot 'summary.json') -Raw | ConvertFrom-Json
    if (!$phaseSummary.success -or !$phaseSummary.allInputsUnchanged) { throw 'Host startup phase observations failed.' }
    $hostStartupPhases = [ordered]@{
        summaryPath = 'host-startup-phases/summary.json'; rawRoot = 'host-startup-phases'
        definitions = $phaseSummary.definitions; comparisons = $phaseSummary.comparisons
        workerReadyInBaseline = $phaseSummary.workerReadyInBaseline
        existingHostStartupBoundary = 'Original startup-host readiness includes the tray, supervisor and simulated worker. Observer phases are a separate instrumented cohort.'
    }
    for ($index = 1; $index -le $StartupRuns; $index++) {
        $order = if ($index % 2) { $builds } else { @($builds[1], $builds[0]) }
        foreach ($case in @(@('startup-ui', 'ui-static'), @('startup-host', 'host'),
            @('integrated-cold', 'integrated-static'), @('integrated-ready', 'integrated-static'))) {
            foreach ($build in $order) { Invoke-Run $build $case[0] $case[1] $index 0 }
        }
    }
}
if (!$SkipSteady) {
    for ($index = 1; $index -le $SteadyRuns; $index++) {
        $order = if ($index % 2) { $builds } else { @($builds[1], $builds[0]) }
        foreach ($scenario in @('static-idle', 'static-scroll', 'model', 'model-scroll', 'integrated-static', 'host')) {
            foreach ($build in $order) { Invoke-Run $build "steady-$scenario" $scenario $index $Seconds }
        }
    }
}
if (!$SkipModels) {
    for ($index = 0; $index -le $ModelProcessRuns; $index++) {
        $order = if ($index % 2) { $builds } else { @($builds[1], $builds[0]) }
        foreach ($build in $order) {
            $sample = if ($index -eq 0) { 'rehearsal' } else { "process-$index" }
            & (Join-Path $PSScriptRoot 'Measure-ModelLoading.ps1') -Executable $build.app `
                -OutputRoot (Join-Path $output "$($build.variant)/model-loading/$sample") `
                -Models free4,dynaudio -Backends vulkan -ColdRuns 1 -HotRuns $ModelHotRuns
        }
    }
}

function Get-Distribution([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if (!$sorted.Count) { return $null }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    return [pscustomobject]@{ count = $sorted.Count; median = $median; p95 = $sorted[[Math]::Ceiling($sorted.Count * .95) - 1] }
}
function Add-Metric($List, $Run, [string]$Metric, $Value) {
    if ($null -ne $Value) { $List.Add([pscustomobject]@{variant=$Run.variant;kind=$Run.kind;metric=$Metric;value=[double]$Value}) }
}
$metrics = [Collections.Generic.List[object]]::new()
foreach ($group in $runs | Where-Object kind -ne 'rehearsal' | Group-Object kind) {
    $signatures = @($group.Group | ForEach-Object {
        $report = $_.report
        "$($report.harnessRevision)|$($report.scenario)|$($report.windowWidth)|$($report.windowHeight)|$($report.renderScaling)|$($report.actualModelBackend)|$($report.modelSha256)"
    } | Sort-Object -Unique)
    if ($signatures.Count -ne 1) { throw "Window geometry/DPI, model, backend or harness differs within $($group.Name)." }
}
if (!$SkipModels) {
    $modelRows = [Collections.Generic.List[object]]::new()
    foreach ($build in $builds) {
        $rows = foreach ($index in 1..$ModelProcessRuns) {
            $processRows = Get-Content -LiteralPath (Join-Path $output "$($build.variant)/model-loading/process-$index/measurements.json") -Raw | ConvertFrom-Json
            foreach ($row in $processRows) { $row.processSample = $index; $row }
        }
        Write-NewJson (Join-Path $output "$($build.variant)/model-loading/measurements.json") @($rows)
        $modelSummary = foreach ($group in $rows | Group-Object backend,model,kind) {
            [pscustomobject]@{
                backend = $group.Group[0].backend; model = $group.Group[0].model; kind = $group.Group[0].kind
                count = $group.Count; processes = $ModelProcessRuns
                totalMilliseconds = Get-Distribution @($group.Group | ForEach-Object { [double]$_.timings.TotalMilliseconds })
            }
        }
        Write-NewJson (Join-Path $output "$($build.variant)/model-loading/summary.json") @($modelSummary)
        foreach ($row in $rows) {
            $modelRows.Add($row)
            $run = [pscustomobject]@{ variant = $build.variant; kind = "model-load-$($row.model)-$($row.kind)" }
            foreach ($property in $row.timings.PSObject.Properties | Where-Object Name -like '*Milliseconds') {
                Add-Metric $metrics $run $property.Name $property.Value
            }
        }
    }
    if (@($modelRows.nativeSha256 | Sort-Object -Unique).Count -ne 1) { throw 'Model runs used different native libraries.' }
    foreach ($model in @('free4', 'dynaudio')) {
        if (@($modelRows | Where-Object model -eq $model | ForEach-Object modelSha256 | Sort-Object -Unique).Count -ne 1) {
            throw "Model input differs between variants: $model"
        }
    }
}
foreach ($run in $runs | Where-Object kind -ne 'rehearsal') {
    Add-Metric $metrics $run 'externalReadyMilliseconds' $run.externalReadyMilliseconds
    Add-Metric $metrics $run 'readyMilliseconds' $run.readyMilliseconds
    $samples = @($run.report.samples)
    if ($samples.Count -ge 2) {
        $first = $samples[0]; $last = $samples[-1]; $elapsed = $last.seconds - $first.seconds
        if ($elapsed -gt 0) { Add-Metric $metrics $run 'cpuPercentOneCore' (($last.cpuMilliseconds - $first.cpuMilliseconds) / ($elapsed * 10)) }
        Add-Metric $metrics $run 'observedPeakWorkingSetBytes' (($samples.workingSetBytes | Measure-Object -Maximum).Maximum)
        Add-Metric $metrics $run 'observedPeakPrivateBytes' (($samples.privateBytes | Measure-Object -Maximum).Maximum)
        Add-Metric $metrics $run 'allocatedBytes' ($last.allocatedBytes - $first.allocatedBytes)
        foreach ($generation in 0..2) {
            Add-Metric $metrics $run "gen$generation-collections" ($last.gcCollections[$generation] - $first.gcCollections[$generation])
        }
    }
    foreach ($property in @('uiCallbackIntervalsMilliseconds', 'bitmapCopyIntervalsMilliseconds')) {
        $distribution = Get-Distribution @($run.report.$property)
        if ($distribution) {
            Add-Metric $metrics $run "$property-median" $distribution.median
            Add-Metric $metrics $run "$property-p95" $distribution.p95
        }
    }
    if ($run.externalSamples.Count) {
        $totals = @($run.externalSamples | ForEach-Object {
            ($_.processes | Measure-Object privateWorkingSetBytes -Sum).Sum
        })
        Add-Metric $metrics $run 'totalPrivateWorkingSetBytes-median' (Get-Distribution $totals).median
        foreach ($identity in $run.identities) {
            $roleSamples = @($run.externalSamples | ForEach-Object { $_.processes } |
                Where-Object processId -eq $identity.processId)
            foreach ($property in @('privateWorkingSetBytes', 'workingSetBytes', 'privateBytes')) {
                $values = @($roleSamples | ForEach-Object { [double]$_.$property })
                if ($values.Count) {
                    Add-Metric $metrics $run "$($identity.role)-$property-median" (Get-Distribution $values).median
                    Add-Metric $metrics $run "$($identity.role)-$property-observedPeak" (($values | Measure-Object -Maximum).Maximum)
                }
            }
        }
        $first = $run.externalSamples[0]; $last = $run.externalSamples[-1]
        $elapsed = $last.seconds - $first.seconds
        if ($elapsed -gt 0) {
            $totalCpu = 0.0
            foreach ($identity in $run.identities) {
                $before = $first.processes | Where-Object processId -eq $identity.processId | Select-Object -First 1
                $after = $last.processes | Where-Object processId -eq $identity.processId | Select-Object -First 1
                if ($before -and $after) {
                    $cpu = ($after.cpuMilliseconds - $before.cpuMilliseconds) / ($elapsed * 10)
                    $totalCpu += $cpu
                    Add-Metric $metrics $run "$($identity.role)-cpuPercentOneCore" $cpu
                }
            }
            Add-Metric $metrics $run 'totalCpuPercentOneCore' $totalCpu
        }
    }
}
$comparisons = foreach ($group in $metrics | Group-Object kind,metric) {
    $baseline = Get-Distribution @($group.Group | Where-Object variant -eq 'r2r-jit' | ForEach-Object value)
    $aot = Get-Distribution @($group.Group | Where-Object variant -eq 'native-aot' | ForEach-Object value)
    [pscustomobject]@{
        kind = $group.Group[0].kind; metric = $group.Group[0].metric; baseline = $baseline; aot = $aot
        changePercent = if ($baseline -and $aot -and $baseline.median -ne 0) { ($aot.median / $baseline.median - 1) * 100 } else { $null }
    }
}
foreach ($build in $builds) {
    if (@(Get-ChildItem -LiteralPath $build.root -File -Recurse).Count -ne $build.fileCount -or
        (Get-FileHash -LiteralPath $build.manifestPath -Algorithm SHA256).Hash -ne $build.manifestSha256) {
        throw "Published file list or manifest changed during measurement: $($build.root)"
    }
    foreach ($file in $build.fileInventory) {
        $path = Join-Path $build.root $file.path
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Published input changed during measurement: $path"
        }
    }
}
Write-NewJson (Join-Path $output 'comparison.json') ([ordered]@{
    schemaVersion = 1; success = $true; allInputsUnchanged = $true; builds = $builds; comparisons = @($comparisons)
    hostStartupPhases = $hostStartupPhases
    publicationSize = [ordered]@{
        baselineBytes = $builds[0].publishedBytes; aotBytes = $builds[1].publishedBytes
        changePercent = ($builds[1].publishedBytes / $builds[0].publishedBytes - 1) * 100
    }
    modelResults = if (!$SkipModels) { @('r2r-jit/model-loading/summary.json', 'native-aot/model-loading/summary.json') } else { @() }
    limitations = @('UI callbacks and bitmap copies are not monitor-present FPS.', 'Fresh process does not imply cold OS caches.',
        'Synthetic worker includes process/stdio/supervision costs; real Bluetooth, catalog-worker and device behavior are excluded.',
        'AOT is not required to be faster. Positive changePercent means a larger measured value.')
})
Write-Output "Comparison saved: $(Join-Path $output 'comparison.json')"
