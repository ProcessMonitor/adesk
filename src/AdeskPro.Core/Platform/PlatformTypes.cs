namespace AdeskPro.Platform;

public sealed record WindowInfo(string Name, int Pid, string ProcessName, long Hwnd);

public sealed class AppNotFoundException(string message) : Exception(message);

public sealed class AmbiguousWindowException(IReadOnlyList<WindowInfo> windows)
    : Exception("multiple windows match the filter")
{
    public IReadOnlyList<WindowInfo> Windows { get; } = windows;
}

public sealed class AmbiguousTargetException(string message) : Exception(message);

public sealed class NoPatternException(string what, string suggestion) : Exception(what)
{
    public string Suggestion { get; } = suggestion;
}
