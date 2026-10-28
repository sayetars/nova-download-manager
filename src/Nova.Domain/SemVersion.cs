namespace Nova.Domain;

/// <summary>
/// Minimal, allocation-light SemVer 2.0.0 (major.minor.patch) parser and comparator.
/// Pre-release/build metadata is accepted and ignored for ordering except that a
/// version WITH pre-release sorts BELOW the same version without one (SemVer §11).
/// Used for update checks and extension↔engine compatibility floors.
/// </summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVersion>
{
    public static SemVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim();
        string? pre = null;
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];              // strip build metadata
        var dash = core.IndexOf('-', 1);
        if (dash >= 0)
        {
            pre = core[(dash + 1)..];
            core = core[..dash];
        }
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        if (major < 0 || minor < 0 || patch < 0) return null;
        return new SemVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
    }

    public static SemVersion Parse(string text) =>
        TryParse(text) ?? throw new FormatException($"Invalid semantic version: '{text}'");

    public bool IsAtLeast(SemVersion floor) => CompareTo(floor) >= 0;

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // no pre-release > with pre-release; both have one → lexicographic on dot-separated ids
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
}
