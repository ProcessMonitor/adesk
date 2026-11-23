using AdeskPro.Platform;

namespace AdeskPro;

/// <summary>The Windows binary entry point: hands the shared CLI the Windows platform adapter.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] argv) => Cli.Run(argv, NewPlatform);

    /// <summary>One adapter per OS. The macOS adapter plugs in here on that build.</summary>
    private static IPlatform NewPlatform() => new Windows.WindowsPlatform();
}
