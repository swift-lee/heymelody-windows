[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$Executable = (Join-Path $PSScriptRoot '../src/HeyMelody.App/bin/Release/net10.0/HeyMelody.exe'),
    [ValidateSet('vulkan', 'opengl')][string[]]$Backends = @('vulkan', 'opengl'),
    [ValidateSet('free4', 'dynaudio', 'online')][string[]]$Models = @('free4', 'dynaudio', 'online'),
    [string]$OnlineModelFile,
    [ValidateRange(1, 30)][int]$ColdRuns = 5,
    [ValidateRange(5, 30)][int]$HotRuns = 5
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputRoot)
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
    throw 'Use a new or empty output directory; previous measurements must be preserved.'
}
if ($Models -contains 'online' -and ([string]::IsNullOrWhiteSpace($OnlineModelFile) -or !(Test-Path -LiteralPath $OnlineModelFile -PathType Leaf))) {
    throw 'OnlineModelFile must be the verified GLB obtained from the current HeyMelody resource service.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$logRoot = Join-Path $output 'logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$session = [ordered]@{
    schemaVersion = 1; startedUtc = [DateTimeOffset]::UtcNow; executable = $exe
    executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceCommit = (& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD)
    backends = $Backends; models = $Models; coldRuns = $ColdRuns; hotRunsPerProcess = $HotRuns
    scope = 'Real HeyMelody.exe isolated production model-control scene; each cold sample is a fresh process; hot samples detach to non-model artwork and reattach; no headset operations or artwork RPC. Full-window steady memory is measured separately.'
}
if ($OnlineModelFile) {
    $session.onlineModelFile = [IO.Path]::GetFullPath($OnlineModelFile)
    $session.onlineModelSha256 = (Get-FileHash -LiteralPath $OnlineModelFile -Algorithm SHA256).Hash.ToLowerInvariant()
}
$session | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'session.json') -Encoding utf8
$rows = [Collections.Generic.List[object]]::new()
foreach ($backend in $Backends) {
    foreach ($model in $Models) {
        for ($index = 1; $index -le $ColdRuns; $index++) {
            $sampleRoot = Join-Path $output "$backend/$model-$index"
            New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null
            $modelArgument = if ($model -eq 'online') { 'file' } else { $model }
            $arguments = @('--model-loading-benchmark', '--backend', $backend, '--model', $modelArgument,
                '--hot-runs', $HotRuns.ToString(), '--output-dir', ('"' + $sampleRoot + '"'))
            if ($model -eq 'online') { $arguments += @('--file', ('"' + [IO.Path]::GetFullPath($OnlineModelFile) + '"')) }
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $logRoot "$backend-$model-$index.stdout.log") `
                -RedirectStandardError (Join-Path $logRoot "$backend-$model-$index.stderr.log")
            try {
                if (!$process.WaitForExit(600000)) {
                    $process.Kill($true)
                    throw "Benchmark process exceeded ten minutes: $sampleRoot"
                }
                $reportPath = Join-Path $sampleRoot 'model-loading.json'
                if (!(Test-Path -LiteralPath $reportPath)) { throw "No benchmark receipt: $sampleRoot (exit $($process.ExitCode))" }
                $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                if ($process.ExitCode -ne 0 -or !$report.success -or $report.runs.Count -ne ($HotRuns + 1)) {
                    throw "Benchmark failed: $sampleRoot; $($report.failure)"
                }
                if ($report.processName -ne 'HeyMelody' -or !$report.nativeLibrarySha256) { throw 'A real application/native process is required.' }
                foreach ($run in $report.runs) {
                    $rows.Add([pscustomobject]@{
                        backend = $backend; model = $model; processSample = $index; kind = $run.kind
                        timings = $run.timings; appSha256 = $report.appAssemblySha256
                        nativeSha256 = $report.nativeLibrarySha256; modelSha256 = $report.modelSha256
                        receipt = $reportPath
                    })
                }
                Write-Output ("{0}/{1} {2}/{3}: cold {4:0.0} ms; hot {5} samples" -f $backend, $model, $index, $ColdRuns, $report.runs[0].timings.TotalMilliseconds, $HotRuns)
            }
            finally { $process.Dispose() }
        }
    }
}
$metrics = @('AttachToLoadStartMilliseconds', 'RetirementWaitMilliseconds', 'GateWaitMilliseconds',
    'ReadVerifyMilliseconds', 'RuntimePreparationMilliseconds', 'ProbeWaitMilliseconds', 'EngineCreateMilliseconds',
    'ModelLoadMilliseconds', 'FirstFrameMilliseconds', 'TotalMilliseconds')
if (@($rows.appSha256 | Sort-Object -Unique).Count -ne 1 -or @($rows.nativeSha256 | Sort-Object -Unique).Count -ne 1) {
    throw 'Application or native binaries changed during measurement. Do not combine these samples.'
}
foreach ($model in $Models) {
    if (@($rows | Where-Object model -eq $model | Select-Object -ExpandProperty modelSha256 -Unique).Count -ne 1) {
        throw "Model bytes changed during measurement: $model"
    }
}
$summary = foreach ($group in ($rows | Group-Object backend, model, kind)) {
    $first = $group.Group[0]
    $stages = [ordered]@{}
    foreach ($metric in $metrics) {
        $values = @($group.Group | ForEach-Object { [double]$_.timings.$metric } | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
        $stages[$metric] = [ordered]@{ median = $median; maximum = $values[-1] }
    }
    [ordered]@{ backend = $first.backend; model = $first.model; kind = $first.kind; count = $group.Count; stages = $stages }
}
$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'measurements.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output ("Complete: {0} measurements, {1}" -f $rows.Count, $output)
