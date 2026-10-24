using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Win32;
using HeyMelodyWindows.App;
using SkiaSharp;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Ui;

/// <summary>Rendering parameters shared by every HeyMelody UI process (compiled into each by source link).</summary>
internal static class RenderingSetup
{
    // Skia's default glyph (strike) cache is about 2 MB; Chinese text uses hundreds of glyphs per size and
    // weight, so a small cache re-rasterises them while scrolling and expanding. This is only a ceiling.
    internal const long FontCacheLimitBytes = 64L * 1024 * 1024;
    // Avalonia's default GPU resource budget is about 28 MiB, below one large hero image plus Lottie layers,
    // WebP frames and the glyph atlas; evicted textures would otherwise be uploaded again.
    internal const long GpuResourceLimitBytes = 256L * 1024 * 1024;

    internal static Win32PlatformOptions CreateWindowsOptions(bool basicRenderMode,
        Func<byte[]?>? resolveWarpLuid = null)
    {
        var options = new Win32PlatformOptions
        {
            // Use the same DXGI first adapter as Avalonia's normal non-ARM64 path, on every Windows architecture.
            // Supplying a callback bypasses Avalonia 12.1.4's default ARM64 Adreno blocklist.
            GraphicsAdapterSelectionCallback = adapters =>
            {
                LogSelectedAdapter(adapters, 0, basicRenderMode: false);
                return 0;
            },
            RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            // Mica needs WinUI composition; both composition modes pace frames at the display refresh rate.
            CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface]
        };

        if (!basicRenderMode)
            return options;

        byte[]? warpLuid;
        try
        {
            warpLuid = (resolveWarpLuid ?? WindowsWarpAdapterResolver.GetAdapterLuid)();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Avalonia.GPU", "warpResolutionFailed", "WARP lookup failed; the existing software fallback is used.", exception);
            Logger.TryGet(LogEventLevel.Warning, "OpenGL")?.Log(null,
                "Unable to resolve the WARP adapter; using software rendering: {Error}", exception);
            warpLuid = null;
        }

        if (warpLuid is not { Length: 8 })
        {
            DiagnosticLog.Warning("Avalonia.GPU", "warpUnavailable", "Basic Render has no WARP LUID; the existing software fallback is used.");
            Logger.TryGet(LogEventLevel.Warning, "OpenGL")?.Log(null,
                "Basic Render was requested, but no WARP adapter LUID is available; using software rendering.");
            options.RenderingMode = [Win32RenderingMode.Software];
            options.GraphicsAdapterSelectionCallback = null;
            return options;
        }

        // Keep the choice immutable even if a caller owns the resolver's returned buffer.
        var expectedLuid = (byte[])warpLuid.Clone();
        options.GraphicsAdapterSelectionCallback = adapters =>
        {
            for (var index = 0; index < adapters.Count; index++)
                if (adapters[index].DeviceLuid is { } luid && luid.AsSpan().SequenceEqual(expectedLuid))
                {
                    LogSelectedAdapter(adapters, index, basicRenderMode: true);
                    return index;
                }

            // D3D11AngleWin32PlatformGraphics.TryCreate catches this and the next RenderingMode is Software.
            // Returning 0 here would silently select hardware despite the user's Basic Render preference.
            DiagnosticLog.Warning("Avalonia.GPU", "warpNotEnumerated", "WARP was absent from ANGLE's adapter list; the existing software fallback is used.",
                data: new { expectedLuid = Convert.ToHexString(expectedLuid), adapters = adapters.Select(DescribeCandidate).ToArray() });
            throw new InvalidOperationException("Basic Render was requested, but the WARP adapter is absent from " +
                "the ANGLE adapter list. Falling back to software rendering.");
        };
        return options;
    }

    private static object DescribeCandidate(PlatformGraphicsDeviceAdapterDescription adapter) => new
    {
        adapter.Description,
        luid = adapter.DeviceLuid is { } luid ? Convert.ToHexString(luid) : null,
        uuid = adapter.DeviceUuid is { } uuid ? Convert.ToHexString(uuid) : null
    };

    private static void LogSelectedAdapter(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters, int index, bool basicRenderMode)
    {
        if (!DiagnosticLog.IsEnabled) return;
        DiagnosticLog.Info("Avalonia.GPU", "adapterSelected", "ANGLE adapter selection callback completed; device creation can still fall back.",
            new { basicRenderMode, selectedIndex = index, adapters = adapters.Select(DescribeCandidate).ToArray(),
                selected = index >= 0 && index < adapters.Count ? WindowsWarpAdapterResolver.DescribeAdapter(adapters[index].DeviceLuid) : null });
    }

    internal static AppBuilder UseHeyMelodyRendering(this AppBuilder builder, bool? basicRenderMode = null)
    {
        var requestedBasicRenderMode = basicRenderMode ?? (OperatingSystem.IsWindows() && UiRenderingPreferenceReader.Load());
        SKGraphics.SetFontCacheLimit(FontCacheLimitBytes);
        DiagnosticLog.Debug("Avalonia.GPU", "configuration", "Applying the existing GUI render configuration.",
            new { basicRenderMode = requestedBasicRenderMode, FontCacheLimitBytes, GpuResourceLimitBytes,
                platform = Environment.OSVersion.Platform.ToString() });
        builder = builder.With(new SkiaOptions { MaxGpuResourceSizeBytes = GpuResourceLimitBytes });
        // Avalonia's default merges every dirty rect of a frame into one union, so the hero animation on the left and any
        // change on the right pane repaint nearly the whole window together. Region tracking keeps them separate.
        builder = builder.With(new CompositionOptions { UseRegionDirtyRectClipping = true, MaxDirtyRects = 8 });
        if (OperatingSystem.IsWindows())
            builder = builder
                .With(CreateWindowsOptions(requestedBasicRenderMode))
                // The D3D9 path does not use GraphicsAdapterSelectionCallback. Never let it bypass Basic Render.
                .With(new AngleOptions { AllowedPlatformApis = [AngleOptions.PlatformApi.DirectX11] });
        return builder.AfterSetup(_ =>
        {
            if (!DiagnosticLog.IsEnabled) return;
            try
            {
                DiagnosticLog.Info("Avalonia.GPU", "platformInitialized", "Avalonia bootstrap completed; concrete graphics services are not exposed by this public bootstrap API.",
                    new { application = Application.Current?.GetType().FullName,
                        graphicsService = (string?)null, renderInterface = (string?)null,
                        detailsAvailable = false });
            }
            catch (Exception error)
            {
                DiagnosticLog.Warning("Avalonia.GPU", "platformDetailsUnavailable", "Graphics service metadata could not be read.", error);
            }
        });
    }
}

/// <summary>Opt-in frame-rate and frame-time overlay for checking 120 Hz behaviour on a real display.</summary>
/// <remarks>
/// Set HEYMELODY_RENDER_DIAGNOSTICS=1 before starting a window, or press Ctrl+Shift+F12 in it. An idle window renders
/// no frames, so the numbers are meaningful only while something animates: an expander, the 3D model or a wizard page.
/// </remarks>
internal static class RenderDiagnostics
{
    internal const string EnvironmentVariable = "HEYMELODY_RENDER_DIAGNOSTICS";
    internal const RendererDebugOverlays Overlays =
        RendererDebugOverlays.Fps | RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.LayoutTimeGraph;

    internal static bool Enabled { get; private set; } = Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";
    internal static event EventHandler? Changed;

    internal static void Toggle() => Set(!Enabled);

    internal static void Set(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Applies the current choice to a window and lets Ctrl+Shift+F12 toggle it for every window.</summary>
    internal static void Attach(Window topLevel)
    {
        void Apply() => topLevel.RendererDiagnostics.DebugOverlays = Enabled ? Overlays : RendererDebugOverlays.None;
        void OnChanged(object? sender, EventArgs e) => Apply();
        Apply();
        Changed += OnChanged;
        topLevel.Closed += (_, _) => Changed -= OnChanged;
        topLevel.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.F12 || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;
            Toggle();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
