using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HeyMelody.Platform;

namespace HeyMelody.Platform.Tests;

internal static class IpcChecks
{
    public static void CrossSessionMutex()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Real os.setsid check applies to Unix session scope.");
        using var scope = new TestDirectory(); var name = DesktopRuntime.MutexName("Test-" + Guid.NewGuid().ToString("N"));
        using var owner = DesktopRuntime.CreateMutex(name, out var created);
        Program.Require(created && owner.WaitOne(0), "The initial isolated singleton could not be acquired.");
        bool held = true;
        try
        {
            using (var child = Program.ReadChild(Program.ChildStart("--mutex-child", name, newSession: true, scope.DirectoryAt("child-one"))))
            {
                var data = child.RootElement;
                Program.Require(data.GetProperty("sessionId").GetInt32() != Program.SessionId, "The child did not enter a different Unix session.");
                Program.Require(data.GetProperty("opened").GetBoolean() && !data.GetProperty("createdNew").GetBoolean(), "Existing singleton was not discovered across processes and sessions.");
                Program.Require(!data.GetProperty("acquired").GetBoolean(), "A second process acquired an owned singleton.");
                Program.Require(data.GetProperty("userKey").GetString() == DesktopRuntime.UserSessionKey, "User identity depends on the Unix session.");
            }
            owner.ReleaseMutex(); held = false;
            using var released = Program.ReadChild(Program.ChildStart("--mutex-child", name, newSession: true, scope.DirectoryAt("child-two")));
            Program.Require(released.RootElement.GetProperty("opened").GetBoolean() && released.RootElement.GetProperty("acquired").GetBoolean(),
                "A later process could not acquire the released singleton.");
        }
        finally { if (held) owner.ReleaseMutex(); }
    }

    public static int MutexChild(string name)
    {
        bool opened = DesktopRuntime.TryOpenMutex(name, out var existing); existing?.Dispose();
        using var mutex = DesktopRuntime.CreateMutex(name, out var createdNew);
        bool acquired = mutex.WaitOne(200);
        if (acquired) mutex.ReleaseMutex();
        Console.WriteLine(JsonSerializer.Serialize(new { opened, createdNew, acquired, sessionId = Program.SessionId, userKey = DesktopRuntime.UserSessionKey }));
        return 0;
    }

    public static async Task AbsolutePipeAsync()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("Absolute Unix-domain pipe paths and TMPDIR isolation.");
        using var scope = new TestDirectory(); var runtime = scope.DirectoryAt("runtime");
        var pipe = DesktopRuntime.GetUnixPipePath(DesktopRuntime.CurrentUserId, macOS: false, runtime);
        DesktopRuntime.ValidateUnixDirectory(Path.GetDirectoryName(pipe)!, create: true);
        string parentTemp = scope.DirectoryAt("server-temp"), childTemp = scope.DirectoryAt("client-temp");
        string? previous = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMPDIR", parentTemp + Path.DirectorySeparatorChar);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var child = Process.Start(Program.ChildStart("--pipe-child", pipe, newSession: false, childTemp))
                ?? throw new IOException("Could not start pipe child.");
            var childOutput = child.StandardOutput.ReadToEndAsync(); var childError = child.StandardError.ReadToEndAsync();
            try
            {
                await server.WaitForConnectionAsync(deadline.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                Program.Require(await reader.ReadLineAsync(deadline.Token) == "ping", "Pipe client sent an unexpected message.");
                await writer.WriteLineAsync("pong".AsMemory(), deadline.Token);
                await child.WaitForExitAsync(deadline.Token);
                Program.Require(child.ExitCode == 0, "Pipe child failed: " + await childError);
                using var result = JsonDocument.Parse(await childOutput);
                Program.Require(result.RootElement.GetProperty("reply").GetString() == "pong", "Child did not receive the server response.");
                Program.Require(result.RootElement.GetProperty("temporaryDirectory").GetString() == childTemp + Path.DirectorySeparatorChar,
                    "The child did not use a distinct TMPDIR.");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        finally { Environment.SetEnvironmentVariable("TMPDIR", previous); }
    }

    public static async Task<int> PipeChildAsync(string pipe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("ping".AsMemory(), deadline.Token);
        string? reply = await reader.ReadLineAsync(deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { reply, temporaryDirectory = Environment.GetEnvironmentVariable("TMPDIR") }));
        return reply == "pong" ? 0 : 1;
    }
}
