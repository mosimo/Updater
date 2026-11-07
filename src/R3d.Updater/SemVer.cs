using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace R3d.Updater;

/// <summary>Minimal semantic version: 1-4 numeric parts, optional "-prerelease", "+build" ignored, optional leading "v".</summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly int[] _parts;
    private readonly string[] _pre;

    private SemVer(int[] parts, string[] pre)
    {
        _parts = parts;
        _pre = pre;
    }

    public int Major => _parts[0];
    public int Minor => _parts[1];
    public int Patch => _parts[2];
    public bool IsPrerelease => _pre.Length > 0;

    public static SemVer Zero { get; } = new([0, 0, 0, 0], []);

    public static SemVer Parse(string s) =>
        TryParse(s, out var v) ? v : throw new FormatException($"Not a valid version: '{s}'");

    public static bool TryParse(string? s, [NotNullWhen(true)] out SemVer? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] pre = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            if (pre.Any(string.IsNullOrEmpty)) return false;
            s = s[..dash];
        }
        var nums = s.Split('.');
        if (nums.Length is < 1 or > 4) return false;
        var parts = new int[4];
        for (var i = 0; i < nums.Length; i++)
            if (!int.TryParse(nums[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
        version = new SemVer(parts, pre);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = _parts[i].CompareTo(other._parts[i]);
            if (c != 0) return c;
        }
        // A release outranks any prerelease with the same number.
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var an = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            int c;
            if (an && bn) c = ai.CompareTo(bi);
            else if (an) c = -1;
            else if (bn) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(_parts[0], _parts[1], _parts[2], _parts[3], string.Join('.', _pre));

    public override string ToString()
    {
        var count = _parts[3] != 0 ? 4 : 3;
        var s = string.Join('.', _parts.Take(count));
        return _pre.Length > 0 ? s + "-" + string.Join('.', _pre) : s;
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
}
