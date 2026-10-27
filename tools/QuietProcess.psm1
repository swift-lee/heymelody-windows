# A process-local policy: no registry or machine-wide error-reporting changes.
function Initialize-QuietProcessPolicy {
    if (-not ('HeyMelody.Tools.QuietProcessTaskObserver' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Threading;
using System.Threading.Tasks;
namespace HeyMelody.Tools {
    public static class QuietProcessTaskObserver {
        public static void Observe(Task task) {
            if (task == null) return;
            _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
'@
    }
    if (-not $IsWindows -or ('HeyMelody.Tools.QuietProcessPolicy' -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace HeyMelody.Tools {
    public sealed class QuietProcessPolicy : IDisposable {
        private readonly uint previousMode;
        private readonly uint previousWerFlags;
        private bool disposed;

        [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] private static extern int WerGetFlags(IntPtr process, out uint flags);
        [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);

        public uint ErrorMode { get; }
        public uint WerFlags { get; }

        public QuietProcessPolicy() {
            previousMode = GetErrorMode();
            int result = WerGetFlags(new IntPtr(-1), out previousWerFlags);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            // Children inherit the process error mode unless they explicitly reset it.
            SetErrorMode(previousMode | 0x8003u);
            result = WerSetFlags((previousWerFlags & ~16u) | 32u);
            if (result < 0) {
                SetErrorMode(previousMode);
                Marshal.ThrowExceptionForHR(result);
            }
            ErrorMode = GetErrorMode();
            result = WerGetFlags(new IntPtr(-1), out uint flags);
            if (result < 0) {
                Dispose();
                Marshal.ThrowExceptionForHR(result);
            }
            WerFlags = flags;
            if ((ErrorMode & 3u) != 3u || (WerFlags & 32u) == 0) {
                Dispose();
                throw new InvalidOperationException("Quiet process policy could not be enabled.");
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            WerSetFlags(previousWerFlags);
            SetErrorMode(previousMode);
        }
    }
}
'@
}

function Write-QuietFailureTail {
    param([string]$Path)
    $tail = [Collections.Generic.Queue[string]]::new()
    $firstFailure = $null
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ($line.Length -eq 0 -or $line.Length -gt 350) { continue }
        # Do not echo terminal bell / escape control characters from child output.
        $cleanLine = $line -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', ' '
        if (-not $firstFailure -and $cleanLine -match '(?i)exception:|unhandled|error [a-z]+[0-9]+|assert|FAIL:') { $firstFailure = $cleanLine }
        $tail.Enqueue($cleanLine)
        if ($tail.Count -gt 8) { [void]$tail.Dequeue() }
    }
    if ($firstFailure -and -not $tail.Contains($firstFailure)) { Write-Host $firstFailure }
    foreach ($line in $tail) { Write-Host $line }
}

function Close-QuietResource {
    param($Resource, [string]$Name, [Collections.Generic.List[string]]$Errors)
    if ($null -eq $Resource) { return }
    try { $Resource.Dispose() }
    catch { $Errors.Add($Name + ': ' + $_.Exception.Message) }
}

function Invoke-QuietProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$ErrorPath,
        [ValidateRange(1,86400)][int]$TimeoutSeconds = 600
    )
    if (-not [IO.Path]::IsPathRooted($FilePath)) { throw 'Use an absolute executable path.' }
    $outputFile = [IO.Path]::GetFullPath($OutputPath)
    $errorFile = [IO.Path]::GetFullPath($ErrorPath)
    if ($outputFile.Equals($errorFile,[StringComparison]::OrdinalIgnoreCase)) { throw 'Output and error logs must be separate files.' }
    foreach ($path in @($outputFile,$errorFile)) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        if ([IO.File]::Exists($path)) { throw "Refusing to overwrite an existing log: $path" }
    }
    $policy = $null
    $process = $null
    $stdoutFile = $null
    $stderrFile = $null
    $stdoutStream = $null
    $stderrStream = $null
    $stdoutCopy = $null
    $stderrCopy = $null
    $copies = $null
    $copyCancellation = $null
    $processStarted = $false
    $processId = $null
    $exitCode = $null
    $timedOut = $false
    $captureSucceeded = $false
    $captureErrors = [Collections.Generic.List[string]]::new()
    $clock = $null
    try {
        Initialize-QuietProcessPolicy
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $policy = if ($IsWindows) { [HeyMelody.Tools.QuietProcessPolicy]::new() } else { $null }
        $process = [Diagnostics.Process]::new()
        $startInfo = [Diagnostics.ProcessStartInfo]::new($FilePath)
        $startInfo.WorkingDirectory = $WorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }
        $process.StartInfo = $startInfo
        $stdoutFile = [IO.File]::Open($outputFile,'CreateNew','Write','ReadWrite')
        $stderrFile = [IO.File]::Open($errorFile,'CreateNew','Write','ReadWrite')
        if (-not $process.Start()) { throw 'The child process did not start.' }
        $processStarted = $true
        $processId = $process.Id
        $stdoutStream = $process.StandardOutput.BaseStream
        $stderrStream = $process.StandardError.BaseStream
        $copyCancellation = [Threading.CancellationTokenSource]::new()
        $stdoutCopy = $stdoutStream.CopyToAsync($stdoutFile,81920,$copyCancellation.Token)
        $stderrCopy = $stderrStream.CopyToAsync($stderrFile,81920,$copyCancellation.Token)
        $copies = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutCopy,$stderrCopy))
        $remaining = [int][math]::Max(0,[math]::Ceiling($TimeoutSeconds * 1000 - $clock.Elapsed.TotalMilliseconds))
        if (-not $process.WaitForExit($remaining)) {
            $timedOut = $true
            $captureErrors.Add('Process exceeded the timeout; output capture was stopped before completion.')
        } else {
            $exitCode = $process.ExitCode
            # The copies share the original process budget, including inherited output handles.
            $remaining = [int][math]::Max(0,[math]::Ceiling($TimeoutSeconds * 1000 - $clock.Elapsed.TotalMilliseconds))
            try { $copied = $copies.Wait($remaining) }
            catch { $copied = $true } # Faulted copies are represented below, not as an invented exit code.
            if (-not $copied) {
                $timedOut = $true
                $captureErrors.Add('Output capture exceeded the timeout; the logs are incomplete.')
            } elseif ($copies.IsCompletedSuccessfully) {
                $captureSucceeded = $true
            } else {
                $detail = if ($copies.Exception) { $copies.Exception.Flatten().Message } else { 'Output capture was cancelled.' }
                $captureErrors.Add($detail)
            }
        }
    } catch {
        if (-not $processStarted) { throw }
        $captureErrors.Add('Process/capture failure: ' + $_.Exception.Message)
    } finally {
        try {
            if ($copyCancellation -and $copies -and -not $copies.IsCompleted) {
                try { $copyCancellation.Cancel() }
                catch { $captureErrors.Add('Capture cancellation failed: ' + $_.Exception.Message) }
            }
            if ($processStarted) {
                try {
                    if (-not $process.HasExited) {
                        $process.Kill($true)
                        # A separate bounded termination grace never extends output capture.
                        if (-not $process.WaitForExit(5000)) { $captureErrors.Add('Owned process termination was not confirmed.') }
                    }
                } catch { $captureErrors.Add('Process cleanup failed: ' + $_.Exception.Message) }
                try { if ($process.HasExited) { $exitCode = $process.ExitCode } }
                catch { $captureErrors.Add('Reading process exit code failed: ' + $_.Exception.Message) }
            }
            Close-QuietResource $stdoutStream 'Closing stdout capture' $captureErrors
            Close-QuietResource $stderrStream 'Closing stderr capture' $captureErrors
            Close-QuietResource $stdoutFile 'Closing stdout log' $captureErrors
            Close-QuietResource $stderrFile 'Closing stderr log' $captureErrors
            Close-QuietResource $copyCancellation 'Closing capture cancellation' $captureErrors
            Close-QuietResource $process 'Closing process handle' $captureErrors
            foreach ($task in @($stdoutCopy,$stderrCopy,$copies)) {
                if ($task) {
                    try { [HeyMelody.Tools.QuietProcessTaskObserver]::Observe($task) }
                    catch { $captureErrors.Add('Observing capture failure: ' + $_.Exception.Message) }
                }
            }
        } catch {
            $captureErrors.Add('Process cleanup failure: ' + $_.Exception.Message)
        } finally {
            if ($policy) {
                try { $policy.Dispose() }
                catch { $captureErrors.Add('Restoring quiet process policy failed: ' + $_.Exception.Message) }
            }
        }
    }
    $result = [pscustomobject]@{
        ProcessId = $processId
        ExitCode = $exitCode
        TimedOut = $timedOut
        CaptureSucceeded = $captureSucceeded -and $captureErrors.Count -eq 0
        CaptureError = $(if ($captureErrors.Count) { $captureErrors -join [Environment]::NewLine } else { $null })
        Seconds = [math]::Round($clock.Elapsed.TotalSeconds,2)
        OutputPath = $outputFile
        ErrorPath = $errorFile
        ErrorMode = $(if ($policy) { $policy.ErrorMode } else { $null })
        WerFlags = $(if ($policy) { $policy.WerFlags } else { $null })
    }
    if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CaptureSucceeded) {
        try {
            Write-Host "Process failed: exit=$($result.ExitCode), timedOut=$($result.TimedOut), captured=$($result.CaptureSucceeded). Logs: $outputFile; $errorFile"
            Write-QuietFailureTail -Path $outputFile
            Write-QuietFailureTail -Path $errorFile
        } catch {
            $result | Add-Member -NotePropertyName FailureSummaryError -NotePropertyValue $_.Exception.Message
        }
    }
    return $result
}

Export-ModuleMember -Function Invoke-QuietProcess
