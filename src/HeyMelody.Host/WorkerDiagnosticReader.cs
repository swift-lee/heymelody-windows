using System.Text;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

/// <summary>Stderr is independent of worker stdout RPC and may contain arbitrary native output.</summary>
internal static class WorkerDiagnosticReader
{
    internal const int MaximumFailureLineCharacters = 256 * 1024;

    internal static async Task DrainAsync(TextReader reader, string role, int processId)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        bool oversized = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                // Preserve every character in chunks, including lines beyond the parser's memory bound.
                if (DiagnosticLog.IsEnabled)
                    DiagnosticLog.Info("worker.stderr", "receive", "Received worker stderr",
                        new { role, processId, text = new string(buffer, 0, count) });
                for (int i = 0; i < count; i++)
                {
                    char value = buffer[i];
                    if (value == '\n')
                    {
                        if (!oversized) Publish(line.ToString().TrimEnd('\r'));
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length < MaximumFailureLineCharacters) line.Append(value);
                        else { line.Clear(); oversized = true; }
                    }
                }
            }
            if (!oversized && line.Length != 0) Publish(line.ToString().TrimEnd('\r'));
        }
        catch (Exception failure)
        {
            DiagnosticLog.Warning("worker.stderr", "ended", "Worker stderr reader ended", failure, new { role, processId });
        }
    }

    private static void Publish(string line)
    {
        try
        {
            if (DiagnosticLog.TryParseFailureLine(line, out var failure) && failure is not null)
                DiagnosticLog.ReportExternalFailure(failure);
        }
        catch (Exception failure)
        {
            // Native stderr is arbitrary text. A malformed diagnostic line must not stop its drain.
            DiagnosticLog.Warning("worker.stderr", "invalidFailure", "Could not parse a worker diagnostic failure line", failure);
        }
    }
}
