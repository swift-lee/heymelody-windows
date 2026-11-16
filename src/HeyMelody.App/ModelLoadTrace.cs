using System.Diagnostics;
using System.Text.Json;

namespace HeyMelodyWindows.App;

/// <summary>Opt-in local measurements for the actual application process, never a default log.</summary>
internal sealed class ModelLoadTrace : IDisposable
{
    internal const string EnvironmentVariable = "HEYMELODY_MODEL_LOAD_TRACE";
    private readonly StreamWriter _writer;

    private ModelLoadTrace(StreamWriter writer)
    {
        _writer = writer;
        DeviceModelView.LoadMeasured += OnMeasured;
    }

    internal static ModelLoadTrace? Start()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new ModelTraceSession("session", 1, Environment.ProcessId,
                Path.GetFileName(Environment.ProcessPath), System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), DateTimeOffset.UtcNow),
                ModelBenchmarkJson.Default.ModelTraceSession));
            var trace = new ModelLoadTrace(writer);
            writer = null;
            stream = null;
            return trace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine($"Model load trace unavailable: {error.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (writer is not null) writer.Dispose();
                else stream?.Dispose();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { /* A failed opt-in log must not keep its file handle or disrupt application startup. */ }
        }
    }

    private void OnMeasured(DeviceModelView view, ModelLoadTimings timings)
    {
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(new ModelTraceLoad("load", Environment.ProcessId,
                DateTimeOffset.UtcNow, view.ModelDigest, timings), ModelBenchmarkJson.Default.ModelTraceLoad));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { Trace.WriteLine($"Model load trace unavailable: {error.Message}"); }
    }

    public void Dispose()
    {
        DeviceModelView.LoadMeasured -= OnMeasured;
        try { _writer.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { Trace.WriteLine($"Model load trace unavailable: {error.Message}"); }
    }
}
