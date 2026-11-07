#Requires -Version 7.5
<#
.SYNOPSIS
Compares opt-in real-window audit runs without starting an application or GPU.
.EXAMPLE
./tools/research/Compare-PerformanceAudit.ps1 -Baseline artifacts/B1,artifacts/B2 -Candidate artifacts/C1,artifacts/C2 -OutputPath artifacts/comparison-NEW.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Baseline,
    [Parameter(Mandatory)][string[]]$Candidate,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-Value($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Test-Finite($Value) {
    if ($null -eq $Value) { return $false }
    $number = 0.0
    return [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and [double]::IsFinite($number)
}

function Get-Distribution([object[]]$Values) {
    $numbers = [Collections.Generic.List[double]]::new()
    foreach ($value in $Values) {
        if (Test-Finite $value) { $numbers.Add([double]$value) }
    }
    $numbers.Sort()
    if ($numbers.Count -eq 0) {
        return [pscustomobject]@{
            count = 0; mean = $null; minimum = $null; median = $null
            p95 = $null; p99 = $null; maximum = $null
        }
    }
    $sum = 0.0
    foreach ($number in $numbers) { $sum += $number }
    $middle = [int][Math]::Floor($numbers.Count / 2.0)
    $median = if ($numbers.Count % 2 -eq 0) {
        ($numbers[$middle - 1] + $numbers[$middle]) / 2.0
    } else { $numbers[$middle] }
    return [pscustomobject]@{
        count = $numbers.Count
        mean = $sum / $numbers.Count
        minimum = $numbers[0]
        median = $median
        p95 = $numbers[[Math]::Ceiling($numbers.Count * .95) - 1]
        p99 = $numbers[[Math]::Ceiling($numbers.Count * .99) - 1]
        maximum = $numbers[-1]
    }
}

function Get-TextMetadata([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    $reader = [IO.File]::OpenText($file.FullName)
    $lines = 0L
    $characters = 0L
    $longest = 0
    try {
        while (($line = $reader.ReadLine()) -ne $null) {
            $lines++
            $characters += $line.Length
            $longest = [Math]::Max($longest, $line.Length)
        }
    } finally { $reader.Dispose() }
    return [pscustomobject]@{
        path = $file.FullName; bytes = $file.Length
        charactersExcludingNewlines = $characters; lines = $lines; longestLine = $longest
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-Intervals([object[]]$Frames) {
    $intervals = [Collections.Generic.List[double]]::new()
    for ($i = 1; $i -lt $Frames.Count; $i++) {
        $intervals.Add(1000.0 * ([double]$Frames[$i].Seconds - [double]$Frames[$i - 1].Seconds))
    }
    return Get-Distribution $intervals.ToArray()
}

function Test-ModelHealthy($Model, [string]$Backend) {
    return $null -ne $Model -and $Model.ModelReady -eq $true -and
        -not $Model.LoadError -and $Model.rendererState -eq 'Ready' -and
        $Model.actualBackend -eq $Backend
}

function Test-AnimationHealthy($Animation) {
    return $null -ne $Animation -and $Animation.AnimationLoaded -eq $true -and
        $Animation.AnimationActive -eq $true -and -not $Animation.LoadError
}

function Read-AuditRun([string]$Directory, [string]$Variant) {
    $directory = (Resolve-Path -LiteralPath $Directory).ProviderPath
    $summaryPath = Join-Path $directory 'summary.json'
    $samplesPath = Join-Path $directory 'samples.jsonl'
    $summaryMetadata = Get-TextMetadata $summaryPath
    $samplesMetadata = Get-TextMetadata $samplesPath
    if ($summaryMetadata.bytes -gt 2MB) { throw "Unexpectedly large summary: $summaryPath" }
    $summary = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json -Depth 64 -DateKind String
    $duration = [double]$summary.measurementSeconds
    if (-not (Test-Finite $duration) -or $duration -le 0) {
        throw "Missing positive measurementSeconds: $summaryPath"
    }
    $issues = [Collections.Generic.List[string]]::new()
    $callbacks = [Collections.Generic.List[object]]::new()
    $processSamples = [Collections.Generic.List[object]]::new()
    $copyEvents = [Collections.Generic.List[object]]::new()
    $kindCounts = [ordered]@{}
    $lineNumber = 0L
    foreach ($line in [IO.File]::ReadLines($samplesPath)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line.Length -gt 2MB) { throw "Unexpectedly long JSONL line $lineNumber in $samplesPath" }
        try { $record = $line | ConvertFrom-Json -Depth 64 -DateKind String }
        catch { throw "Invalid JSONL at line $lineNumber in $samplesPath (source text not printed)." }
        $kind = [string]$record.kind
        if (-not $kindCounts.Contains($kind)) { $kindCounts[$kind] = 0L }
        $kindCounts[$kind]++
        $item = switch ($kind) {
            'uiCallback' { $record.callback }
            'processAndModel' { $record.sample }
            'bitmapCopy' { $record.frame }
            default { $null }
        }
        if ($null -eq $item) { continue }
        if (-not (Test-Finite $item.Seconds) -or $item.Seconds -lt 0 -or
            $item.Seconds -gt $duration + .000001) {
            $issues.Add("Out-of-window or invalid timestamp at JSONL line $lineNumber.")
            continue
        }
        switch ($kind) {
            'uiCallback' { $callbacks.Add($item) }
            'processAndModel' { $processSamples.Add($item) }
            'bitmapCopy' { $copyEvents.Add($item) }
        }
    }
    if ($summary.success -ne $true) { $issues.Add('Harness did not report success.') }
    if ($summary.nativeBluetoothCalls -ne 0 -or $summary.productionBackendStarted -ne $false -or
        $summary.forbiddenServiceCalls -ne 0) { $issues.Add('Device/backend isolation assertion failed.') }
    if ($callbacks.Count -ne $summary.uiCallbacks.count) { $issues.Add('UI callback count disagrees with summary.') }
    $foregroundGood = @($processSamples | Where-Object {
        $_.WindowIsActive -eq $true -and $_.Desktop.ForegroundProcessId -eq $summary.process.processId
    }).Count
    if ($processSamples.Count -eq 0 -or $foregroundGood -ne $processSamples.Count) {
        $issues.Add('A discrete process sample was not active/foreground, or no samples were recorded.')
    }
    $expectsModel = $summary.scenario -in @('model', 'model-scroll', 'model-frozen')
    $expectsAnimation = $summary.scenario -eq 'animated'
    if ($summary.scenario -notin @('static', 'static-idle', 'static-scroll', 'animated',
            'model', 'model-scroll', 'model-frozen')) {
        $issues.Add('Unknown audit scenario.')
    }
    $modelAtStart = Get-Value $summary 'modelAtStart'
    $modelAtEnd = Get-Value $summary 'modelAtEnd'
    $animationAtStart = Get-Value $summary 'animatedArtworkAtStart'
    $animationAtEnd = Get-Value $summary 'animatedArtworkAtEnd'
    if ($expectsModel -and ($null -eq $modelAtStart -or $null -eq $modelAtEnd)) {
        $issues.Add('Model scenario is missing an endpoint model record.')
    } elseif (-not $expectsModel -and ($null -ne $modelAtStart -or $null -ne $modelAtEnd)) {
        $issues.Add('No-model scenario contains an endpoint model record.')
    }
    if ($expectsAnimation -and ($null -eq $animationAtStart -or $null -eq $animationAtEnd)) {
        $issues.Add('Animated scenario is missing an endpoint animation record.')
    } elseif (-not $expectsAnimation -and ($null -ne $animationAtStart -or $null -ne $animationAtEnd)) {
        $issues.Add('Non-animated scenario contains an endpoint animation record.')
    }
    $invalidModelSamples = 0
    $invalidAnimationSamples = 0
    foreach ($sample in $processSamples) {
        if ($expectsModel) {
            if (-not (Test-ModelHealthy $sample.Model $summary.requestedBackend)) { $invalidModelSamples++ }
        } elseif ($null -ne $sample.Model) { $invalidModelSamples++ }
        if ($expectsAnimation) {
            if (-not (Test-AnimationHealthy $sample.AnimatedArtwork)) { $invalidAnimationSamples++ }
        } elseif ($null -ne $sample.AnimatedArtwork) { $invalidAnimationSamples++ }
    }
    if ($invalidModelSamples -gt 0) { $issues.Add('Process samples violate scenario model presence/health requirements.') }
    if ($invalidAnimationSamples -gt 0) { $issues.Add('Process samples violate scenario animation presence/health requirements.') }
    if (-not $expectsModel -and $copyEvents.Count -gt 0) {
        $issues.Add('No-model scenario emitted bitmap-copy events.')
    }
    $artwork = Get-Value $summary 'artwork'
    $modelSha = [string](Get-Value $artwork 'ModelDigest')
    $animationSha = [string](Get-Value $artwork 'sha256')
    if ($expectsModel -and $modelSha -notmatch '^[a-fA-F0-9]{64}$') {
        $issues.Add('Model input SHA256 is missing or invalid.')
    }
    if ($expectsAnimation -and $animationSha -notmatch '^[a-fA-F0-9]{64}$') {
        $issues.Add('Animation input SHA256 is missing or invalid.')
    }
    $renderer = Get-Value $summary 'actualAvaloniaRenderer'
    if ($null -eq $renderer -or $renderer.Queried -ne $true -or $renderer.Failure) {
        $issues.Add('Actual Avalonia renderer query is missing or failed.')
    }
    for ($i = 1; $i -lt $callbacks.Count; $i++) {
        if ($callbacks[$i].Seconds -le $callbacks[$i - 1].Seconds) {
            $issues.Add('UI callback timestamps are not strictly increasing.')
            break
        }
    }
    $uiIntervals = Get-Intervals $callbacks.ToArray()
    $model = $null
    if ($null -ne $summary.modelAtStart) {
        $first = $summary.modelAtStart
        $last = $summary.modelAtEnd
        $delta = [ordered]@{}
        foreach ($key in @('AnimationCallbackCount', 'CameraUpdateCount', 'PresentedFrameCount',
                'BitmapLockCount', 'DirectFrameCopyCount', 'FallbackFrameCopyCount',
                'FrameProbeCount', 'FrameProbeNoFrameCount', 'completedFrameId', 'presentedFrameId')) {
            $a = Get-Value $first $key
            $b = Get-Value $last $key
            $delta[$key] = if ($null -ne $a -and $null -ne $b) { [long]$b - [long]$a } else { $null }
            if ($null -ne $delta[$key] -and $delta[$key] -lt 0) { $issues.Add("Counter regressed: $key") }
        }
        $invalidHealth = $invalidModelSamples
        if (-not (Test-ModelHealthy $first $summary.requestedBackend) -or
            -not (Test-ModelHealthy $last $summary.requestedBackend) -or $invalidHealth -ne 0) {
            $issues.Add('Model readiness/backend health assertion failed.')
        }
        if ($first.Width -ne $last.Width -or $first.Height -ne $last.Height -or
            $first.MsaaSamples -ne $last.MsaaSamples) {
            $issues.Add('Model viewport or MSAA changed during measurement.')
        }
        $policy = Get-Value $summary 'modelRenderPolicy'
        $cap = Get-Value $policy 'maximumLongEdgePixels'
        if ($null -ne $cap -and $cap -gt 0 -and
            ([Math]::Max($first.Width, $first.Height) -gt $cap -or
             [Math]::Max($last.Width, $last.Height) -gt $cap)) {
            $issues.Add('Actual model viewport exceeds the requested physical-pixel cap.')
        }
        $observations = [Collections.Generic.List[object]]::new()
        $seen = [Collections.Generic.HashSet[ulong]]::new()
        $baselineId = [ulong]$first.presentedFrameId
        $previousId = $baselineId
        $regressions = 0
        foreach ($callback in $callbacks) {
            if ($null -eq $callback.ModelFrameId) { continue }
            $id = [ulong]$callback.ModelFrameId
            if ($id -lt $previousId) { $regressions++ }
            $previousId = $id
            if ($id -gt $baselineId -and $seen.Add($id)) { $observations.Add($callback) }
        }
        if ($regressions -gt 0) { $issues.Add('Observed bitmap frame ID regressed.') }
        $exact = [int]$summary.schemaVersion -ge 3 -and $null -ne (Get-Value $summary 'bitmapCopies')
        $frames = @(if ($exact) { $copyEvents.ToArray() } else { $observations.ToArray() })
        $strictIds = $true
        $previousId = $baselineId
        $previousSeconds = -1.0
        foreach ($frame in $frames) {
            $id = if ($exact) { [ulong]$frame.FrameId } else { [ulong]$frame.ModelFrameId }
            if ($id -le $previousId -or $frame.Seconds -lt $previousSeconds) { $strictIds = $false }
            $previousId = $id
            $previousSeconds = [double]$frame.Seconds
        }
        if (-not $strictIds) { $issues.Add('New-copy IDs or timestamps are not monotonic.') }
        if ($frames.Count -gt 0) {
            $lastId = if ($exact) { [ulong]$frames[-1].FrameId } else { [ulong]$frames[-1].ModelFrameId }
            if ($lastId -ne [ulong]$last.presentedFrameId) {
                $issues.Add('Last measured bitmap ID disagrees with the endpoint presentedFrameId.')
            }
        } elseif ($baselineId -ne [ulong]$last.presentedFrameId) {
            $issues.Add('Endpoint bitmap ID changed without a measured new-copy event/observation.')
        }
        $copyCount = [long]$delta.PresentedFrameCount
        $copies = [long]$delta.DirectFrameCopyCount + [long]$delta.FallbackFrameCopyCount
        $noCopyLocks = [long]$delta.BitmapLockCount - $copies
        if ($copyCount -ne $copies) { $issues.Add('Presented/direct/fallback copy counter deltas disagree.') }
        if ($noCopyLocks -lt 0) { $issues.Add('Copy count exceeds bitmap lock count.') }
        $copyIntervals = Get-Intervals $frames
        if ($exact) {
            if ($copyEvents.Count -ne $copyCount -or $copyEvents.Count -ne $summary.bitmapCopies.count) {
                $issues.Add('Exact copy events disagree with endpoint/summary copy counts.')
            }
            if ((Get-Value $summary.bitmapCopies 'frameIdsStrictlyIncreasing') -eq $false) {
                $issues.Add('Harness reported non-increasing exact copy IDs.')
            }
            if ($copyIntervals.count -ne $summary.bitmapCopies.intervalCount) {
                $issues.Add('Exact copy interval count disagrees with summary.')
            }
            if ($null -ne (Get-Value $summary.bitmapCopies 'hertz') -and
                [Math]::Abs($copyCount / $duration - $summary.bitmapCopies.hertz) -gt .000001) {
                $issues.Add('Recomputed copy hertz disagrees with summary.')
            }
            if ($null -ne $copyIntervals.p95 -and
                [Math]::Abs($copyIntervals.p95 - $summary.bitmapCopies.p95Milliseconds) -gt .000001) {
                $issues.Add('Recomputed copy P95 disagrees with summary.')
            }
        }
        if ($summary.scenario -eq 'model-frozen' -and
            ($copyCount -ne 0 -or $delta.CameraUpdateCount -ne 0 -or $delta.completedFrameId -ne 0)) {
            $issues.Add('Frozen model advanced camera/render/copy counters.')
        }
        $model = [pscustomobject]@{
            start = [pscustomobject]@{ width = $first.Width; height = $first.Height; msaa = $first.MsaaSamples }
            end = [pscustomobject]@{ width = $last.Width; height = $last.Height; msaa = $last.MsaaSamples }
            counterDelta = $delta; successfulCopyCount = $copyCount; successfulCopyHertz = $copyCount / $duration
            intervalSource = if ($exact) { 'successful-copy observer' } else { 'audit-RAF observed new bitmap IDs (proxy)' }
            newFrameIntervalsMilliseconds = $copyIntervals; exactCopyEventCount = $copyEvents.Count
            uniqueObservedNewBitmapIds = $observations.Count; frameIdsStrictlyIncreasing = $strictIds
            observedFrameIdRegressions = $regressions
            lockWithoutSuccessfulCopyCount = $noCopyLocks
            lockWithoutSuccessfulCopyRatio = if ($delta.BitmapLockCount -gt 0) {
                $noCopyLocks / [double]$delta.BitmapLockCount
            } else { $null }
            invalidProcessHealthSamples = $invalidHealth
        }
    }
    $animation = $null
    if ($null -ne $summary.animatedArtworkAtStart) {
        $invalidHealth = $invalidAnimationSamples
        if (-not (Test-AnimationHealthy $summary.animatedArtworkAtStart) -or
            -not (Test-AnimationHealthy $summary.animatedArtworkAtEnd) -or $invalidHealth -ne 0) {
            $issues.Add('Animated-artwork health assertion failed.')
        }
        $animation = [pscustomobject]@{
            actualDrawCountDelta = $summary.animatedArtworkDrawCountDelta
            actualDrawCallbacksPerSecond = $summary.animatedArtworkDrawCountDelta / $duration
            invalidProcessHealthSamples = $invalidHealth
        }
    }
    $window = $summary.window
    $geometry = [ordered]@{
        widthDip = $window.Width; heightDip = $window.Height; renderScaling = $window.RenderScaling
        physicalWidth = $window.physicalClientWidth; physicalHeight = $window.physicalClientHeight
    }
    $theme = Get-Value $summary 'requestedTheme'
    if ($null -eq $theme) { $theme = Get-Value $window 'theme' }
    $signature = [ordered]@{
        harnessRevision = $summary.harnessRevision
        architecture = $summary.process.architecture; processorCount = $summary.process.ProcessorCount
        managedRuntime = $summary.process.runtime
        requestedSeconds = $summary.requestedSeconds; warmupSeconds = $summary.warmupSeconds
        basicRender = $summary.requestedBasicRenderMode; window = $geometry
        modelMsaa = if ($model) { $model.start.msaa } else { $null }
        modelSha256 = if ($expectsModel) { $modelSha.ToLowerInvariant() } else { $null }
        animationSha256 = if ($expectsAnimation) { $animationSha.ToLowerInvariant() } else { $null }
        artworkKind = Get-Value $artwork 'kind'; artworkSource = Get-Value $artwork 'source'
        avaloniaRenderer = [ordered]@{
            queried = Get-Value $renderer 'Queried'; hasGpuContext = Get-Value $renderer 'HasGpuContext'
            vendor = Get-Value $renderer 'Vendor'; renderer = Get-Value $renderer 'Renderer'
            version = Get-Value $renderer 'Version'; failure = Get-Value $renderer 'Failure'
        }
    } | ConvertTo-Json -Depth 5 -Compress
    return [pscustomobject]@{
        variant = $Variant; name = [IO.Path]::GetFileName($directory); directory = $directory
        schemaVersion = $summary.schemaVersion; harnessRevision = $summary.harnessRevision
        scenario = $summary.scenario; backend = $summary.requestedBackend; theme = $theme
        comparisonKey = "$($summary.scenario)|$($summary.requestedBackend)|$theme"
        inputSignature = $signature; modelRenderPolicy = Get-Value $summary 'modelRenderPolicy'
        measurementSeconds = $duration; measurementStartedUtc = $summary.measurementStartedUtc
        measurementEndedUtc = $summary.measurementEndedUtc; geometry = $geometry
        cpuPercentOneCore = $summary.process.cpuPercentOneCore
        cpuPercentMachine = $summary.process.cpuPercentMachine
        allocatedMiB = $summary.process.allocatedBytes / 1MB
        peakWorkingSetMiB = $summary.process.peakWorkingSetBytes / 1MB
        peakPrivateMiB = $summary.process.peakPrivateBytes / 1MB
        uiCallbackHertz = $callbacks.Count / $duration; uiCallbackIntervalsMilliseconds = $uiIntervals
        foregroundActiveSamples = $foregroundGood; processSamples = $processSamples.Count
        model = $model; animation = $animation; kindCounts = $kindCounts
        valid = $issues.Count -eq 0; issues = $issues.ToArray()
        inputs = [pscustomobject]@{ summary = $summaryMetadata; samples = $samplesMetadata }
    }
}

function Get-Metric($Run, [string]$Name) {
    switch ($Name) {
        'successfulCopyHertz' { if ($Run.model) { return $Run.model.successfulCopyHertz } }
        'newFrameP95Milliseconds' { if ($Run.model) { return $Run.model.newFrameIntervalsMilliseconds.p95 } }
        'uiCallbackHertz' { return $Run.uiCallbackHertz }
        'uiCallbackP95Milliseconds' { return $Run.uiCallbackIntervalsMilliseconds.p95 }
        'cpuPercentOneCore' { return $Run.cpuPercentOneCore }
        'allocatedMiB' { return $Run.allocatedMiB }
        'peakWorkingSetMiB' { return $Run.peakWorkingSetMiB }
        'artworkDrawCallbacksPerSecond' { if ($Run.animation) { return $Run.animation.actualDrawCallbacksPerSecond } }
    }
    return $null
}

$output = [IO.Path]::GetFullPath($OutputPath, (Get-Location).ProviderPath)
if (Test-Path -LiteralPath $output) { throw 'OutputPath must be a new file; existing evidence is never overwritten.' }
$rows = [Collections.Generic.List[object]]::new()
$seenInputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($variant in @('B', 'C')) {
    $paths = if ($variant -eq 'B') { $Baseline } else { $Candidate }
    foreach ($path in $paths) {
        $resolved = (Resolve-Path -LiteralPath $path).ProviderPath
        if (-not $seenInputs.Add($resolved)) { throw "A run cannot be used more than once: $resolved" }
        $prefix = $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'OutputPath cannot be inside an input run directory.'
        }
        $rows.Add((Read-AuditRun $resolved $variant))
    }
}
$comparisons = foreach ($group in ($rows | Group-Object comparisonKey | Sort-Object Name)) {
    $b = @($group.Group | Where-Object variant -eq 'B')
    $c = @($group.Group | Where-Object variant -eq 'C')
    $warnings = [Collections.Generic.List[string]]::new()
    $compatible = $true
    if ($b.Count -eq 0 -or $c.Count -eq 0) {
        $warnings.Add('No baseline/candidate counterpart.')
        $compatible = $false
    }
    if (@($group.Group.inputSignature | Sort-Object -Unique).Count -gt 1) {
        $warnings.Add('Harness/input SHA/compositor, architecture/runtime, duration, basic-render mode, window geometry or MSAA differs.')
        $compatible = $false
    }
    $sources = @($group.Group | Where-Object { $_.model } |
        ForEach-Object { $_.model.intervalSource } | Sort-Object -Unique)
    if ($sources.Count -gt 1) {
        $warnings.Add('Mixed successful-copy timing and RAF-observation proxies; intervals are not equivalent.')
        $compatible = $false
    }
    if (@($group.Group | Where-Object { -not $_.valid }).Count -gt 0) {
        $warnings.Add('One or more runs failed health/integrity checks.')
        $compatible = $false
    }
    if ($b.Count -lt 2 -or $c.Count -lt 2) { $warnings.Add('Fewer than two repeats per variant; variability is not established.') }
    $metrics = [ordered]@{}
    foreach ($name in @('successfulCopyHertz', 'newFrameP95Milliseconds', 'uiCallbackHertz',
            'uiCallbackP95Milliseconds', 'cpuPercentOneCore', 'allocatedMiB',
            'peakWorkingSetMiB', 'artworkDrawCallbacksPerSecond')) {
        $bs = Get-Distribution @($b | ForEach-Object { Get-Metric $_ $name })
        $cs = Get-Distribution @($c | ForEach-Object { Get-Metric $_ $name })
        $metrics[$name] = [pscustomobject]@{
            baseline = $bs; candidate = $cs
            medianPercentChange = if ($compatible -and $null -ne $bs.median -and $bs.median -gt 0 -and $null -ne $cs.median) {
                100.0 * ($cs.median / $bs.median - 1.0)
            } else { $null }
        }
    }
    [pscustomobject]@{
        key = $group.Name; baselineRuns = $b.Count; candidateRuns = $c.Count
        percentageChangesAvailable = $compatible
        warnings = $warnings.ToArray(); metrics = $metrics
    }
}
$report = [ordered]@{
    schemaVersion = 1; createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    analyzerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    allRunsValid = @($rows | Where-Object { -not $_.valid }).Count -eq 0
    rows = $rows.ToArray(); comparisons = @($comparisons)
    scope = 'Offline comparison only. Successful bitmap copies, audit RAF callbacks and artwork draw callbacks are not monitor-present FPS.'
    intervalMethod = 'Consecutive measured successful-copy timestamps for schema 3; deduplicated observed IDs above start ID for legacy schema 2. P95 is sorted[ceil(0.95*N)-1]; no rolling-window percentiles.'
    limitations = @(
        'Legacy RAF observations can miss successful bitmap copies between audit callbacks.'
        'CPU is one-core-equivalent unless explicitly machine-normalized; working set retains the harness semantics.'
        'No GPU timestamps, thermal trace, displayed-FPS, input-latency or visual-quality claim is made.'
        'Physical render resolution/policy intentionally may differ; verify image quality and requested frame-rate parity independently.'
        'Per-scenario run medians are descriptive only; small repeat counts do not establish statistical significance.'
    )
}
$parent = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($parent)) { [void][IO.Directory]::CreateDirectory($parent) }
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
try { $writer.WriteLine(($report | ConvertTo-Json -Depth 16)) } finally { $writer.Dispose() }
Write-Output "Wrote $output"
Write-Output "Runs=$($rows.Count), invalid=$(@($rows | Where-Object { -not $_.valid }).Count). Hz below means copied bitmaps, not presented FPS."
foreach ($row in ($rows | Select-Object -First 25)) {
    $copyHz = if ($row.model) { '{0:F2}' -f $row.model.successfulCopyHertz } else { 'n/a' }
    $p95 = if ($row.model -and $null -ne $row.model.newFrameIntervalsMilliseconds.p95) {
        '{0:F3}' -f $row.model.newFrameIntervalsMilliseconds.p95
    } else { 'n/a' }
    Write-Output ('{0} {1,-14} copyHz={2,-7} newP95ms={3,-8} CPU1={4,6:F2}% valid={5}' -f
        $row.variant, $row.scenario, $copyHz, $p95, $row.cpuPercentOneCore, $row.valid)
}
if ($rows.Count -gt 25) { Write-Output 'Console rows limited to 25; JSON contains all runs.' }
