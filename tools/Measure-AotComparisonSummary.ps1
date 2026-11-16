#Requires -Version 7.5
<#
.SYNOPSIS
Summarizes the completed comparison's controller receipts without launching apps.
.DESCRIPTION
Each combined memory series sums the selected processes within the same external
sample before calculating its distribution. Run medians are combined with equal
weight; observedPeak is an observed sample maximum, not an OS peak counter.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$resultRoot = (Resolve-Path -LiteralPath $ResultDirectory).Path
$comparison = Get-Content -LiteralPath (Join-Path $resultRoot 'comparison.json') -Raw | ConvertFrom-Json
if (!$comparison.success -or !$comparison.allInputsUnchanged) { throw 'A successful completed comparison is required.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $resultRoot ('analysis-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must not exist.' }
$controllers = @(Get-ChildItem -LiteralPath $resultRoot -Recurse -File -Filter 'controller.json' | Sort-Object FullName)
if (!$controllers.Count) { throw 'No controller receipts found.' }

function Get-Distribution([object[]]$Values) {
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object {
        $number = [double]$_
        if (![double]::IsFinite($number)) { throw 'A sample contains a nonfinite value.' }
        $number
    } | Sort-Object)
    if (!$numbers.Count) { return [pscustomobject]@{ count=0; median=$null; p95=$null; observedPeak=$null } }
    $middle = [int][Math]::Floor($numbers.Count / 2)
    $median = if ($numbers.Count % 2) { $numbers[$middle] } else { ($numbers[$middle-1] + $numbers[$middle]) / 2 }
    [pscustomobject]@{
        count=$numbers.Count; median=$median
        p95=$numbers[[Math]::Ceiling($numbers.Count*.95)-1]; observedPeak=$numbers[-1]
    }
}

$details = [Collections.Generic.List[object]]::new()
$rows = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
foreach ($file in $controllers) {
    $run = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if (!$run.report.success -or $run.report.runId -ne $run.runId) { throw "Invalid receipt: $($file.FullName)" }
    $inputs.Add([pscustomobject]@{
        path=[IO.Path]::GetRelativePath($resultRoot,$file.FullName)
        sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    $internal = @($run.report.samples)
    $allocated = $null; $gc = $null
    if ($internal.Count -ge 2) {
        $allocated = [long]$internal[-1].allocatedBytes - [long]$internal[0].allocatedBytes
        $gc = @(foreach ($generation in 0..2) { [long]$internal[-1].gcCollections[$generation] - [long]$internal[0].gcCollections[$generation] })
    }
    $intervals = [pscustomobject]@{
        uiCallbacks=Get-Distribution @($run.report.uiCallbackIntervalsMilliseconds)
        bitmapCopies=Get-Distribution @($run.report.bitmapCopyIntervalsMilliseconds)
    }
    $runRows = [Collections.Generic.List[object]]::new()
    $samples = @($run.externalSamples)
    $roles = @($run.identities.role | Sort-Object -Unique)
    $cohorts = [Collections.Generic.List[string]]::new()
    foreach ($role in $roles) { $cohorts.Add($role) }
    if ('tray' -in $roles -and 'worker' -in $roles) { $cohorts.Add('tray+worker') }
    if ('app' -in $roles -and 'tray' -in $roles -and 'worker' -in $roles) { $cohorts.Add('app+tray+worker') }
    foreach ($cohort in $cohorts) {
        $selectedRoles = $cohort.Split('+')
        $expected = @($run.identities | Where-Object role -in $selectedRoles)
        $series = @(foreach ($sample in $samples) {
            if ($sample.complete -eq $false) { throw "Incomplete external sample in $($file.FullName)" }
            $selected = @($sample.processes | Where-Object { $_.processId -in @($expected.processId) })
            if ($selected.Count -ne $expected.Count -or @($selected.processId | Sort-Object -Unique).Count -ne $expected.Count) {
                throw "Missing/duplicate process in cohort $cohort at $($file.FullName)"
            }
            $ws = 0L; $private = 0L; $cpu = 0.0
            foreach ($process in $selected) {
                if ($null -eq $process.privateWorkingSetBytes -or $null -eq $process.privateBytes -or $null -eq $process.cpuMilliseconds) {
                    throw "Missing counter in cohort $cohort at $($file.FullName)"
                }
                $ws += [long]$process.privateWorkingSetBytes
                $private += [long]$process.privateBytes
                $cpu += [double]$process.cpuMilliseconds
            }
            [pscustomobject]@{seconds=[double]$sample.seconds; privateWorkingSetBytes=$ws; privateBytes=$private; cpuMilliseconds=$cpu}
        })
        $cpuPercent = $null; $elapsed = $null
        if ($series.Count -ge 2) {
            $elapsed = $series[-1].seconds - $series[0].seconds
            $cpuDelta = $series[-1].cpuMilliseconds - $series[0].cpuMilliseconds
            if ($elapsed -le 0 -or $cpuDelta -lt 0) { throw "Invalid CPU/time series in $($file.FullName)" }
            $cpuPercent = $cpuDelta / ($elapsed * 10)
        }
        $wsStats = Get-Distribution @($series.privateWorkingSetBytes)
        $privateStats = Get-Distribution @($series.privateBytes)
        $row = [pscustomobject]@{
            runId=$run.runId; variant=$run.variant; kind=$run.kind; scenario=$run.scenario; sample=$run.sample; cohort=$cohort
            externalSamples=$series.Count; elapsedSeconds=$elapsed; cpuPercentOneCore=$cpuPercent
            privateWorkingSetMedianBytes=$wsStats.median; privateWorkingSetObservedPeakBytes=$wsStats.observedPeak
            privateBytesMedian=$privateStats.median; privateBytesObservedPeak=$privateStats.observedPeak
        }
        $runRows.Add($row); $rows.Add($row)
    }
    $details.Add([pscustomobject]@{
        runId=$run.runId; variant=$run.variant; kind=$run.kind; scenario=$run.scenario; sample=$run.sample
        isRehearsal=$run.kind -eq 'rehearsal'; controller=[IO.Path]::GetRelativePath($resultRoot,$file.FullName)
        allocationScope='Primary report process only; do not assign these GC/allocation counts to combined cohorts.'
        primaryProcessId=$run.report.process.processId; internalSamples=$internal.Count
        allocatedBytes=$allocated; gcCollections=$gc; intervals=$intervals; cohorts=$runRows.ToArray()
    })
}
$summary = @(foreach ($group in $rows | Where-Object { $_.externalSamples -gt 0 -and $_.kind -ne 'rehearsal' } |
    Group-Object variant,kind,scenario,cohort) {
    $first = $group.Group[0]
    $wsMedians = Get-Distribution @($group.Group.privateWorkingSetMedianBytes)
    $privateMedians = Get-Distribution @($group.Group.privateBytesMedian)
    $cpu = Get-Distribution @($group.Group.cpuPercentOneCore)
    [pscustomobject]@{
        variant=$first.variant; kind=$first.kind; scenario=$first.scenario; cohort=$first.cohort; runs=$group.Count
        privateWorkingSetMedianOfRunMediansBytes=$wsMedians.median
        privateWorkingSetObservedPeakBytes=($group.Group.privateWorkingSetObservedPeakBytes | Measure-Object -Maximum).Maximum
        privateBytesMedianOfRunMedians=$privateMedians.median
        privateBytesObservedPeak=($group.Group.privateBytesObservedPeak | Measure-Object -Maximum).Maximum
        cpuPercentOneCoreMedian=$cpu.median; cpuPercentOneCoreP95=$cpu.p95; cpuRuns=$cpu.count
    }
})
[IO.Directory]::CreateDirectory($output) | Out-Null
$rows.ToArray() | Export-Csv -LiteralPath (Join-Path $output 'run-metrics.csv') -NoTypeInformation -Encoding utf8
$summary | Export-Csv -LiteralPath (Join-Path $output 'summary.csv') -NoTypeInformation -Encoding utf8
[ordered]@{
    schemaVersion=1; createdUtc=[DateTimeOffset]::UtcNow; resultDirectory=$resultRoot; controllerCount=$controllers.Count
    notes=@('Combined counters are summed within each external sample before median/observedPeak.',
        'Summary medians give equal weight to run medians; summary observedPeak is the largest observed combined sample.',
        'CPU 100 means one logical core; cumulative CPU differences use the same selected processes at the first/last sample.',
        'Rehearsals are retained in runs and excluded from summary; startup runs with no external samples retain null counter statistics.',
        'GC/allocation and UI/bitmap interval distributions remain per run and belong to the primary report process.')
    inputs=$inputs.ToArray(); runs=$details.ToArray(); summary=$summary
} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'analysis.json') -Encoding utf8
Write-Output "Offline analysis saved: $output"
