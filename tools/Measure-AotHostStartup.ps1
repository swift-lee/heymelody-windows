#Requires -Version 7.5
<#
.SYNOPSIS
Observes isolated published Host startup through a real passive IPC subscription.
.DESCRIPTION
The first successful schema-5 subscription is the IPC-ready boundary. The first
current-generation ControlReady snapshot is the simulated worker-ready boundary.
The tray visibility request precedes pipe startup in product code; this tool makes
no claim about Shell pixel presentation. All timestamps use one controller clock.
#>
[CmdletBinding()]
param(
    [string]$BaselineDirectory,
    [string]$AotDirectory,
    [string]$OutputRoot,
    [ValidateRange(1,100)][int]$StartupRuns = 20,
    [ValidateRange(1,50)][int]$PollMilliseconds = 5,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Get-PhaseDistribution([object[]]$Values) {
    $valuesSorted = @($Values | Sort-Object)
    if (!$valuesSorted.Count) { return $null }
    $middle = [int][Math]::Floor($valuesSorted.Count / 2)
    $median = if ($valuesSorted.Count % 2) { $valuesSorted[$middle] } else { ($valuesSorted[$middle-1]+$valuesSorted[$middle])/2 }
    [pscustomobject]@{count=$valuesSorted.Count;median=$median;p95=$valuesSorted[[Math]::Ceiling($valuesSorted.Count*.95)-1];maximum=$valuesSorted[-1]}
}
function Get-CurrentReadySnapshot($Snapshots, [string]$BackendId, $Versions) {
    foreach ($snapshot in @($Snapshots)) {
        if (!$snapshot) { continue }
        if ($snapshot.backendInstanceId -cne $BackendId) { throw 'Snapshot belongs to a different backend instance.' }
        if (!$snapshot.identity) { if ($snapshot.controlReady) { throw 'Ready snapshot has no identity.' }; continue }
        $generation = [long]$snapshot.deviceGeneration; $revision = [long]$snapshot.revision
        if ($generation -le 0 -or $revision -le 0) {
            if ($snapshot.controlReady) { throw 'Ready snapshot has unknown generation/revision.' }
            continue
        }
        $previous = $Versions[$snapshot.identity]
        if ($previous -and ($generation -lt $previous.generation -or
            ($generation -eq $previous.generation -and $revision -lt $previous.revision))) { continue }
        $Versions[$snapshot.identity] = @{generation=$generation;revision=$revision}
        if ($snapshot.controlReady) {
            return [pscustomobject]@{identity=$snapshot.identity;backendInstanceId=$BackendId;deviceGeneration=$generation;revision=$revision}
        }
    }
    return $null
}
if ($SelfTest) {
    $checks=0
    function Check([bool]$Condition,[string]$Message) { if(!$Condition){throw $Message};$script:checks++ }
    $state=@{}
    $ready=[pscustomobject]@{identity='fixture';backendInstanceId='backend';deviceGeneration=1;revision=2;controlReady=$true}
    Check ($null -ne (Get-CurrentReadySnapshot @($ready) 'backend' $state)) 'Ready baseline was rejected.'
    $stale=[pscustomobject]@{identity='fixture';backendInstanceId='backend';deviceGeneration=1;revision=1;controlReady=$true}
    Check ($null -eq (Get-CurrentReadySnapshot @($stale) 'backend' $state)) 'Stale revision was accepted.'
    $new=[pscustomobject]@{identity='fixture';backendInstanceId='backend';deviceGeneration=2;revision=1;controlReady=$true}
    Check ((Get-CurrentReadySnapshot @($new) 'backend' $state).deviceGeneration -eq 2) 'New generation was rejected.'
    $waiting=[pscustomobject]@{identity='pending';backendInstanceId='backend';deviceGeneration=0;revision=0;controlReady=$false}
    Check ($null -eq (Get-CurrentReadySnapshot @($waiting) 'backend' @{})) 'Unknown waiting state became ready.'
    foreach($bad in @(
        [pscustomobject]@{identity='fixture';backendInstanceId='old';deviceGeneration=1;revision=1;controlReady=$true},
        [pscustomobject]@{identity='fixture';backendInstanceId='backend';deviceGeneration=0;revision=1;controlReady=$true},
        [pscustomobject]@{identity='';backendInstanceId='backend';deviceGeneration=1;revision=1;controlReady=$true}
    )) {
        $rejected=$false
        try { [void](Get-CurrentReadySnapshot @($bad) 'backend' @{}) } catch { $rejected=$true }
        Check $rejected 'An invalid ready snapshot was accepted.'
    }
    $distribution=Get-PhaseDistribution @(2.0,8.0,4.0,6.0)
    Check ($distribution.count-eq4-and$distribution.median-eq5-and$distribution.p95-eq8) 'Distribution mismatch.'
    [pscustomobject]@{success=$true;checks=$checks;processesStarted=0;headsetCalls=0}|ConvertTo-Json -Compress
    return
}
if (!$IsWindows) { throw 'Windows is required.' }
if (!$BaselineDirectory -or !$AotDirectory -or !$OutputRoot) { throw 'BaselineDirectory, AotDirectory and OutputRoot are required.' }
$output=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $output){throw 'OutputRoot must not exist.'}

function Get-HostBuild([string]$Directory,[string]$Variant) {
    $root=(Resolve-Path -LiteralPath $Directory).Path
    $binary=if(Test-Path -LiteralPath (Join-Path $root 'x64/HeyMelody.exe')){Join-Path $root 'x64'}else{$root}
    $manifestPath=Join-Path $root 'manifest.json'
    if(!(Test-Path -LiteralPath $manifestPath)){$manifestPath=Join-Path (Split-Path $root) 'manifest.json'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    $mode=if($Variant-eq'r2r-jit'){'ReadyToRun'}else{'AotExperimental'}
    if($manifest.compilationMode-ne$mode){throw "Invalid publication mode for $Variant."}
    $exe=Join-Path $binary 'backend/HeyMelody.Host.exe'
    $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if(@($manifest.builds|Where-Object{ $_.rid-eq'win-x64'-and$_.hostSha256-eq$hash }).Count-ne1){throw 'Host does not match one win-x64 manifest entry.'}
    [pscustomobject]@{variant=$Variant;exe=$exe;sha256=$hash;manifest=$manifestPath;manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Write-NewPhaseJson([string]$Path,$Value) {
    if(Test-Path -LiteralPath $Path){throw "Evidence exists: $Path"}
    $Value|ConvertTo-Json -Depth 25|Set-Content -LiteralPath $Path -Encoding utf8
}
function Get-IsolatedPipeName([string]$RunId) {
    $user=[Environment]::UserDomainName+'\'+[Environment]::UserName
    $hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($user))).Substring(0,24)
    $self=[Diagnostics.Process]::GetCurrentProcess()
    try { 'HeyMelody-benchmark-'+$hash+'-'+$self.SessionId+'-'+$RunId } finally{$self.Dispose()}
}
function Assert-PhaseIdentity($Identity,$Build,[string]$Role,$Process=$null) {
    $dynamic=$Build.variant-eq'r2r-jit'
    if(!$Identity-or$Identity.role-ne$Role-or$Identity.architecture-ne'X64'-or
        [bool]$Identity.dynamicCodeSupported-ne$dynamic-or[bool]$Identity.dynamicCodeCompiled-ne$dynamic-or
        ![string]::Equals($Identity.executable,$Build.exe,[StringComparison]::OrdinalIgnoreCase)){
        throw "Invalid $Role identity."
    }
    if(!$Process){$Process=Get-Process -Id $Identity.processId -ErrorAction Stop}
    if($Process.HasExited-or$Process.Id-ne$Identity.processId-or
        $Process.StartTime.ToUniversalTime().Ticks-ne$Identity.startedUtcTicks-or
        ![string]::Equals($Process.Path,$Build.exe,[StringComparison]::OrdinalIgnoreCase)){throw "PID/path/start-time mismatch for $Role."}
    $null=$Process.Handle
    $Process
}
if(!('HeyMelody.AotStartup.NativePipe'-as[type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace HeyMelody.AotStartup {
  public static class NativePipe {
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
  }
}
'@
}
$builds=@(Get-HostBuild $BaselineDirectory 'r2r-jit';Get-HostBuild $AotDirectory 'native-aot')
[IO.Directory]::CreateDirectory($output)|Out-Null
$definitions=[ordered]@{
    ipcSubscriptionObservedMs='First successful schema-5 observer subscription reply; tray visibility was requested before pipe startup by product code order, not observed Shell pixels.'
    workerIdentityObservedMs='First readable and PID/path/start-time-validated worker identity; this does not establish completed initialization.'
    workerControlReadyObservedMs='First baseline or snapshot event containing the current backend/device generation/revision with ControlReady=true.'
    fullReadyReceiptObservedMs='First readable complete Host ready.json: tray plus supervisor plus simulated worker ready.'
    timing='All observations use the same controller Stopwatch started immediately before Process.Start. Poll interval is nominal; scheduler/IPC delays remain.'
    coincident='If the subscription baseline is already ready, IPC and worker-ready observations have the same timestamp. No exclusive worker initialization duration is inferred.'
}
Write-NewPhaseJson (Join-Path $output 'session.json') ([ordered]@{
    schemaVersion=1;startedUtc=[DateTimeOffset]::UtcNow;builds=$builds;startupRuns=$StartupRuns;pollMilliseconds=$PollMilliseconds
    stopwatchFrequency=[Diagnostics.Stopwatch]::Frequency;observerRole='observer';definitions=$definitions
    toolSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
})
$runs=[Collections.Generic.List[object]]::new()
function Invoke-HostPhaseRun($Build,[int]$Index) {
    $rehearsal=$Index-eq0;$label=if($rehearsal){'rehearsal'}else{'run-'+$Index.ToString('D3')}
    $directory=Join-Path $output "$($Build.variant)/$label";[IO.Directory]::CreateDirectory($directory)|Out-Null
    $hostOutput=Join-Path $directory 'host';$runId=[Guid]::NewGuid().ToString('N');$pipeName=Get-IsolatedPipeName $runId
    $hostProcess=$null;$worker=$null;$workerIdentity=$null;$hostIdentity=$null;$pipe=$null;$writer=$null;$reader=$null
    $stdout=$null;$stderr=$null;$issues=[Collections.Generic.List[string]]::new();$messages=[Collections.Generic.List[object]]::new()
    $ipcTicks=$null;$workerTicks=$null;$identityTicks=$null;$fullTicks=$null;$source=$null;$evidence=$null;$backend=$null
    $versions=@{};$childStops=[Collections.Generic.List[object]]::new()
    $start=[Diagnostics.ProcessStartInfo]::new($Build.exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.WorkingDirectory=Split-Path $Build.exe
    foreach($arg in @('--performance-benchmark','--run-id',$runId,'--output-dir',$hostOutput,'--scenario','host','--seconds','0','--diagnostics','false')){$start.ArgumentList.Add($arg)}
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        $hostProcess=[Diagnostics.Process]::Start($start);$null=$hostProcess.Handle
        $stdout=$hostProcess.StandardOutput.ReadToEndAsync();$stderr=$hostProcess.StandardError.ReadToEndAsync()
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,
            [IO.Pipes.PipeOptions]::Asynchronous-bor[IO.Pipes.PipeOptions]::CurrentUserOnly)
        $connect=$pipe.ConnectAsync(45000)
        $pending=$null;$subscribed=$false;$fullReport=$null
        while($null-eq$workerTicks-or$null-eq$fullTicks-or$null-eq$identityTicks) {
            if($hostProcess.HasExited){throw "Host exited before observations completed: $($hostProcess.ExitCode)"}
            if($clock.Elapsed.TotalSeconds-gt45){throw 'Host phase observation timed out.'}
            $workerPath=Join-Path $hostOutput 'worker.json'
            if($null-eq$identityTicks-and(Test-Path -LiteralPath $workerPath)){
                $candidate=$null
                try{$candidate=Get-Content -LiteralPath $workerPath -Raw|ConvertFrom-Json}catch{}
                if($candidate){$worker=Assert-PhaseIdentity $candidate $Build 'worker';$workerIdentity=$candidate;$identityTicks=$clock.ElapsedTicks}
            }
            if(!$subscribed-and$connect.IsCompleted) {
                $connect.GetAwaiter().GetResult()
                [uint32]$serverPid=0
                if(![HeyMelody.AotStartup.NativePipe]::GetNamedPipeServerProcessId($pipe.SafePipeHandle,[ref]$serverPid)-or$serverPid-ne$hostProcess.Id){
                    throw 'The isolated pipe belongs to a different process or cannot be identified.'
                }
                $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true);$writer.AutoFlush=$true
                $reader=[IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
                $writer.WriteLine('{"schemaVersion":5,"id":1,"operation":"subscribe","role":"observer"}')
                $pending=$reader.ReadLineAsync();$subscribed=$true
            }
            if($pending-and$pending.IsCompleted) {
                $line=$pending.GetAwaiter().GetResult();$observed=$clock.ElapsedTicks
                if($null-eq$line){throw 'Observer IPC closed before worker readiness.'}
                $message=$line|ConvertFrom-Json;$messages.Add([pscustomobject]@{observedTicks=$observed;line=$line})
                if($message.schemaVersion-ne5){throw 'Unexpected observer protocol schema.'}
                if($message.kind-eq'response'-and$message.id-eq1) {
                    if(!$message.success-or!$message.data.backendInstanceId){throw 'Observer subscription failed.'}
                    $ipcTicks=$observed;$backend=$message.data.backendInstanceId
                    $candidate=Get-CurrentReadySnapshot $message.data.snapshots $backend $versions
                    if($candidate){$workerTicks=$observed;$evidence=$candidate;$source='subscriptionBaseline'}
                } elseif($message.kind-eq'event') {
                    if(!$backend){throw 'An event preceded the atomic subscription baseline.'}
                    if($message.event.backendInstanceId-cne$backend){throw 'Event belongs to a different backend.'}
                    if($message.event.kind-eq'snapshot'){
                        $candidate=Get-CurrentReadySnapshot @($message.event.snapshot) $backend $versions
                        if($candidate-and$null-eq$workerTicks){$workerTicks=$observed;$evidence=$candidate;$source='snapshotEvent'}
                    }
                } else {throw 'Unexpected observer RPC message.'}
                $pending=if($null-eq$workerTicks){$reader.ReadLineAsync()}else{$null}
            }
            $readyPath=Join-Path $hostOutput 'ready.json'
            if($null-eq$fullTicks-and(Test-Path -LiteralPath $readyPath)){
                $fullReport=Get-Content -LiteralPath $readyPath -Raw|ConvertFrom-Json
                if(!$fullReport.success-or$fullReport.runId-ne$runId-or$fullReport.diagnosticsEnabled){throw 'Invalid Host complete-ready receipt.'}
                [void](Assert-PhaseIdentity $fullReport.process $Build 'tray' $hostProcess)
                $hostIdentity=$fullReport.process;$fullTicks=$clock.ElapsedTicks
            }
            if($null-eq$workerTicks-or$null-eq$fullTicks-or$null-eq$identityTicks){Start-Sleep -Milliseconds $PollMilliseconds}
        }
        if($null-eq$ipcTicks){throw 'No successful observer baseline was observed.'}
    } catch {$issues.Add($_.Exception.Message)}
    finally {
        $workerAliveBeforeStop=$worker-and!$worker.HasExited
        $hostAliveBeforeStop=$hostProcess-and!$hostProcess.HasExited
        if($worker-and!$workerAliveBeforeStop){$issues.Add('Worker ended before the controller requested Host shutdown.')}
        if($hostProcess-and!$hostAliveBeforeStop){$issues.Add('Host ended before the controller requested shutdown.')}
        if($reader){$reader.Dispose()};if($writer){$writer.Dispose()};if($pipe){$pipe.Dispose()}
        [IO.Directory]::CreateDirectory($hostOutput)|Out-Null;[IO.File]::WriteAllText((Join-Path $hostOutput 'stop'),'')
        foreach($process in @($hostProcess,$worker)){
            if(!$process){continue}
            $ended=$process.WaitForExit(15000)
            if(!$ended){$process.Kill($true);[void]$process.WaitForExit(5000);$issues.Add("Isolated PID $($process.Id) required forced termination.")}
            $code=if($process.HasExited){$process.ExitCode}else{$null}
            $role=if($process-eq$hostProcess){'tray'}else{'worker'}
            $childStops.Add([pscustomobject]@{
                processId=$process.Id;role=$role;stoppedWithoutControllerKill=$ended
                aliveBeforeHostStop=if($role-eq'worker'){$workerAliveBeforeStop}else{$hostAliveBeforeStop}
                exitCodeAvailable=$null-ne$code;exitCode=$code
                note=if($role-eq'worker'-and$code-eq-1){'Current fixture refuses shutdown; Host PeerWorker disposal uses its kill fallback. This is not a graceful worker exit.'}else{$null}
            })
            if(($role-eq'tray'-and$code-ne0)-or($null-ne$code-and$code-ne0-and!($role-eq'worker'-and$code-eq-1))){
                $issues.Add("Isolated PID $($process.Id) exited $code.")
            }
        }
        foreach($pair in @(@('stdout',$stdout),@('stderr',$stderr))){
            if($pair[1]){if($pair[1].Wait(5000)){[IO.File]::WriteAllText((Join-Path $directory "$($pair[0]).log"),$pair[1].GetAwaiter().GetResult())}else{$issues.Add('Output pipe did not close.')}}
        }
        if($hostProcess){$hostProcess.Dispose()};if($worker){$worker.Dispose()}
    }
    $factor=1000.0/[Diagnostics.Stopwatch]::Frequency
    $row=[pscustomobject]@{
        schemaVersion=1;success=$issues.Count-eq0;issues=$issues.ToArray();variant=$Build.variant;sample=$Index;rehearsal=$rehearsal;runId=$runId
        pipeName=$pipeName;observerRole='observer';hostIdentity=$hostIdentity;workerIdentity=$workerIdentity;childStops=$childStops.ToArray()
        ipcSubscriptionObservedMs=if($null-ne$ipcTicks){$ipcTicks*$factor}else{$null}
        workerIdentityObservedMs=if($null-ne$identityTicks){$identityTicks*$factor}else{$null}
        workerControlReadyObservedMs=if($null-ne$workerTicks){$workerTicks*$factor}else{$null}
        fullReadyReceiptObservedMs=if($null-ne$fullTicks){$fullTicks*$factor}else{$null}
        ticks=[ordered]@{ipcSubscription=$ipcTicks;workerIdentity=$identityTicks;workerControlReady=$workerTicks;fullReadyReceipt=$fullTicks}
        workerReadySource=$source;workerWasReadyInBaseline=$source-eq'subscriptionBaseline';readySnapshot=$evidence
        exclusiveWorkerInitializationMilliseconds=$null
    }
    Write-NewPhaseJson (Join-Path $directory 'observer-messages.json') $messages.ToArray()
    Write-NewPhaseJson (Join-Path $directory 'run.json') $row
    $runs.Add($row)
    if(!$row.success){throw "Host phase observation failed at $directory : $($row.issues-join'; ')"}
    Write-Output "$($Build.variant) Host phase $label complete"
}
foreach($build in $builds){Invoke-HostPhaseRun $build 0}
for($index=1;$index-le$StartupRuns;$index++){
    $order=if($index%2){$builds}else{@($builds[1],$builds[0])}
    foreach($build in $order){Invoke-HostPhaseRun $build $index}
}
$comparisons=@(foreach($metric in @('ipcSubscriptionObservedMs','workerIdentityObservedMs','workerControlReadyObservedMs','fullReadyReceiptObservedMs')){
    $b=Get-PhaseDistribution @($runs|Where-Object{$_.variant-eq'r2r-jit'-and!$_.rehearsal}|ForEach-Object{$_.$metric})
    $a=Get-PhaseDistribution @($runs|Where-Object{$_.variant-eq'native-aot'-and!$_.rehearsal}|ForEach-Object{$_.$metric})
    [pscustomobject]@{metric=$metric;baseline=$b;aot=$a;changePercent=if($b.median-ne0){($a.median/$b.median-1)*100}else{$null}}
})
foreach($build in $builds){
    if((Get-FileHash -LiteralPath $build.exe -Algorithm SHA256).Hash-ne$build.sha256-or
        (Get-FileHash -LiteralPath $build.manifest -Algorithm SHA256).Hash-ne$build.manifestSha256){throw 'Published Host inputs changed.'}
}
$runs.ToArray()|Select-Object variant,sample,rehearsal,ipcSubscriptionObservedMs,workerIdentityObservedMs,workerControlReadyObservedMs,fullReadyReceiptObservedMs,workerReadySource|
    Export-Csv -LiteralPath (Join-Path $output 'measurements.csv') -NoTypeInformation -Encoding utf8
Write-NewPhaseJson (Join-Path $output 'summary.json') ([ordered]@{
    schemaVersion=1;success=$true;allInputsUnchanged=$true;definitions=$definitions;startupRuns=$StartupRuns
    comparisons=$comparisons;rawDirectories=@('r2r-jit','native-aot')
    workerReadyInBaseline=@(foreach($variant in @('r2r-jit','native-aot')){
        [pscustomobject]@{variant=$variant;count=@($runs|Where-Object{$_.variant-eq$variant-and!$_.rehearsal-and$_.workerWasReadyInBaseline}).Count}
    })
})
Write-Output "Host startup phase summary: $(Join-Path $output 'summary.json')"
