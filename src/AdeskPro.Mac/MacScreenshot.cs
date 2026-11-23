using System.Diagnostics;

namespace AdeskPro.Mac;

/// <summary>Window capture through the system <c>screencapture</c> tool.
/// It targets one window by CGWindowID, respects Retina scaling, and needs no image library —
/// which keeps this adapter free of any drawing/UI dependency.</summary>
internal static class MacScreenshot
{
    /// <summary>Captures one window as PNG, addressed by its CGWindowID — which is exactly what
    /// <c>WindowInfo.Hwnd</c> carries on macOS.</summary>
    public static byte[] CapturePng(long windowId)
    {
        if (windowId <= 0)
            throw new ArgumentException("no window id: the app may be minimised or off screen");

        var output = Path.Combine(Path.GetTempPath(), $"adesk-pro-{Guid.NewGuid():N}.png");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/sbin/screencapture",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("-x");          // no sound
            startInfo.ArgumentList.Add("-o");          // no drop shadow
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("png");
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(windowId.ToString());
            startInfo.ArgumentList.Add(output);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("failed to start screencapture");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new ArgumentException(
                    "screencapture produced no image — grant Screen Recording permission to this terminal " +
                    "in System Settings > Privacy & Security > Screen Recording" +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $" ({stderr.Trim()})"));

            return File.ReadAllBytes(output);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
    }
}
