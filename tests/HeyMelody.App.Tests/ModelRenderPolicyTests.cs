using Avalonia;
using HeyMelodyWindows.App;

internal static class ModelRenderPolicyTests
{
    internal static int Run()
    {
        Run("all platforms use the same model pixel budget and metadata probes", () =>
        {
            Check(ModelRenderPolicy.PlatformDefault == new ModelRenderPolicy(800, true),
                "Production uses an 800-pixel long edge and metadata probes without an OS-specific branch.");
            Check(ModelRenderPolicy.PlatformDefault.GetPixelSize(new Size(600, 520), 1.75) == new PixelSize(800, 693),
                "The default policy caps backing pixels without persisted settings.");
        });
        Run("the uncapped policy preserves physical-pixel rounding", () =>
        {
            Check(new ModelRenderPolicy(0, false).GetPixelSize(new Size(700.25, 606.25), 1.5) == new PixelSize(1051, 910),
                "The baseline still rounds each physical dimension upwards.");
        });
        Run("a landscape model uses one scale for both backing dimensions", () =>
        {
            var pixels = new ModelRenderPolicy(800, true).GetPixelSize(new Size(1050, 910), 1);
            Check(pixels == new PixelSize(800, 693), "A 1050 by 910 viewport gets an 800 by 693 backing frame.");
            Check(Math.Abs(pixels.Height - 910d * 800 / 1050) <= .5, "Aspect error stays within half a backing pixel.");
        });
        Run("a portrait model preserves the same camera aspect", () =>
        {
            Check(new ModelRenderPolicy(800, true).GetPixelSize(new Size(910, 1050), 1) == new PixelSize(693, 800),
                "Portrait views are uniformly scaled instead of independently clamping both dimensions.");
        });
        Run("small viewports are not upscaled", () =>
        {
            Check(new ModelRenderPolicy(800, true).GetPixelSize(new Size(300, 200), 1.25) == new PixelSize(375, 250),
                "The cap never adds backing pixels to a small viewport.");
        });
        Run("cross-screen DPI changes recompute the same pixel budget", () =>
        {
            var policy = new ModelRenderPolicy(800, true);
            Check(policy.GetPixelSize(new Size(700, 600), 1) == new PixelSize(700, 600), "A low-DPI view remains native-sized.");
            Check(policy.GetPixelSize(new Size(700, 600), 1.5) == new PixelSize(800, 686), "A higher DPI reaches the cap.");
            Check(policy.GetPixelSize(new Size(700, 600), 2) == new PixelSize(800, 686), "A further DPI increase keeps the same budget.");
        });
        Run("the pixel cap and metadata probe are independent audit factors", () =>
        {
            var size = new Size(1400, 1200);
            Check(new ModelRenderPolicy(800, true).GetPixelSize(size, 1)
                == new ModelRenderPolicy(800, false).GetPixelSize(size, 1), "Probing does not change geometry.");
            Check(new ModelRenderPolicy(800, true).GetPixelSize(new Size(1, 16000), 1) == new PixelSize(1, 800),
                "A very narrow viewport retains at least one pixel in each dimension.");
        });
        Run("invalid model pixel policies and scaling are rejected", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new ModelRenderPolicy(-1, false).GetPixelSize(new Size(10, 10), 1));
            Throws<ArgumentOutOfRangeException>(() => new ModelRenderPolicy(800, true).GetPixelSize(new Size(0, 10), 1));
            Throws<ArgumentOutOfRangeException>(() => new ModelRenderPolicy(800, true).GetPixelSize(new Size(10, 10), double.NaN));
            Throws<ArgumentOutOfRangeException>(() => new ModelRenderPolicy(800, true).GetPixelSize(new Size(10, 10), 0));
        });
        return 8;

        static void Run(string name, Action action)
        {
            action();
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
