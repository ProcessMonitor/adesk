using System.Security.Cryptography;

namespace Adesk;

/// <summary>Snapshot and ref identifiers: snapshot ids are short opaque tokens, refs are @&lt;sid&gt;:e&lt;n&gt;.</summary>
public static class Ids
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string NewSnapshotId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        long mixed = BitConverter.ToInt64(bytes) ^ Environment.TickCount64 ^ Environment.ProcessId;
        return "s" + Base36(Math.Abs((long)(mixed & long.MaxValue)));
    }

    private static string Base36(long value)
    {
        if (value == 0) return "0";
        Span<char> buf = stackalloc char[13];
        var i = buf.Length;
        while (value > 0)
        {
            buf[--i] = Digits[(int)(value % 36)];
            value /= 36;
        }
        var text = new string(buf[i..]);
        return text.Length < 4 ? text.PadLeft(4, '0') : text;
    }

    public static string FormatRef(string snapshotId, int index) => $"@{snapshotId}:e{index}";

    /// <summary>Parses "@s8f3k2p9:e8". Returns null when the token is not a well-formed ref.</summary>
    public static (string SnapshotId, int Index)? ParseRef(string token)
    {
        if (token.Length < 6 || token[0] != '@') return null;
        var colon = token.IndexOf(':');
        if (colon <= 2 || colon == token.Length - 1) return null;
        var sid = token[1..colon];
        if (!sid.StartsWith('s') || sid.Length < 2 || sid.Length > 64) return null;
        foreach (var c in sid)
            if (!(char.IsAsciiLetterOrDigit(c))) return null;
        if (!token[(colon + 1)..].StartsWith('e')) return null;
        if (!int.TryParse(token[(colon + 2)..], out var index) || index <= 0) return null;
        return (sid, index);
    }

    public static bool ValidSnapshotId(string sid) =>
        sid.Length is >= 2 and <= 64 && sid.StartsWith('s')
        && sid.All(char.IsAsciiLetterOrDigit);
}
