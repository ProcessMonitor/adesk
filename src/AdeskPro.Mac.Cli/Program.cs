using AdeskPro.Platform;

namespace AdeskPro.Mac.Cli;

/// <summary>The macOS binary entry point: hands the shared CLI the macOS platform adapter.</summary>
public static class Program
{
    public static int Main(string[] argv) =>
        // Permission prompting happens inside the CLI, and only for commands that declare
        // NeedsAccessibility — never for --help, --version, status or list-apps.
        AdeskPro.Cli.Run(argv, () => new MacPlatform());
}
