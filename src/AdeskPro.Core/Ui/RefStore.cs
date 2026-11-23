using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdeskPro.Ui;

/// <summary>What an agent needs to re-identify one element later, possibly in another process.</summary>
public sealed class RefEntry
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("processName")] public string ProcessName { get; set; } = "";
    [JsonPropertyName("hwnd")] public long Hwnd { get; set; }
    /// <summary>Name of the window the snapshot walked — used to re-find it among same-pid candidates.</summary>
    [JsonPropertyName("windowName")] public string WindowName { get; set; } = "";
    [JsonPropertyName("runtimeId")] public int[] RuntimeId { get; set; } = [];
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    /// <summary>Stable machine role id captured at snapshot time. Empty for snapshots written by
    /// older builds; identity matching then falls back to <see cref="Role"/>.</summary>
    [JsonPropertyName("roleId")] public string RoleId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("automationId")] public string AutomationId { get; set; } = "";
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("scopePath")] public List<int> ScopePath { get; set; } = [];
    [JsonPropertyName("patterns")] public List<string> Patterns { get; set; } = [];
    [JsonPropertyName("capturedUnique")] public bool CapturedUnique { get; set; }
    [JsonPropertyName("rect")] public int[] Rect { get; set; } = [];

    public string IdentityKey =>
        $"{Pid}|{(RoleId.Length > 0 ? RoleId : Role)}|{AutomationId}|{Name}|{ClassName}";
}

/// <summary>
/// Session-scoped snapshot store. MCP mode keeps everything in memory (no file locks at all);
/// CLI mode persists one JSON file per snapshot so a ref from one process resolves in the next.
/// </summary>
public sealed class RefStore
{
    private const int MaxSnapshots = 50;
    private readonly string? _dir;
    private readonly Dictionary<string, Dictionary<int, RefEntry>> _memory = new();
    /// <summary>Insertion order, so "latest" means the most recently written snapshot —
    /// snapshot ids are random tokens, not sortable.</summary>
    private readonly List<string> _memoryOrder = new();

    public RefStore(string? dir) { _dir = dir; }

    private static string PathFor(string dir, string sid) =>
        Path.Combine(dir, "snapshots", sid + ".json");

    public string? LatestSnapshotId
    {
        get
        {
            if (_memory.Count > 0) return _memoryOrder[^1];
            if (_dir is null) return null;
            var latest = Path.Combine(_dir, "latest.txt");
            return File.Exists(latest) ? File.ReadAllText(latest).Trim() : null;
        }
    }

    public void Save(string snapshotId, Dictionary<int, RefEntry> entries)
    {
        // Re-writing an existing id moves it to the end: "latest" must follow write order.
        _memoryOrder.Remove(snapshotId);
        _memoryOrder.Add(snapshotId);
        _memory[snapshotId] = entries;
        if (_dir is null) { PruneMemory(); return; }
        var path = PathFor(_dir, snapshotId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entries, EnvelopeJson.Options);
        File.WriteAllText(path, json);
        File.WriteAllText(Path.Combine(_dir, "latest.txt"), snapshotId);
        PruneDisk();
    }

    public Dictionary<int, RefEntry>? Load(string snapshotId)
    {
        if (_memory.TryGetValue(snapshotId, out var entries)) return entries;
        if (_dir is null) return null;
        var path = PathFor(_dir, snapshotId);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<int, RefEntry>>(json, EnvelopeJson.Options);
    }

    public Dictionary<int, RefEntry>? LoadLatest() => LatestSnapshotId is { } sid ? Load(sid) : null;

    public int MemorySnapshotCount => _memory.Count;

    private void PruneMemory()
    {
        while (_memoryOrder.Count > MaxSnapshots)
        {
            var oldest = _memoryOrder[0];
            _memoryOrder.RemoveAt(0);
            _memory.Remove(oldest);
        }
    }

    private void PruneDisk()
    {
        if (_dir is null) return;
        var dir = Path.Combine(_dir, "snapshots");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxSnapshots);
        foreach (var f in files) { try { f.Delete(); } catch (IOException) { } }
    }
}
