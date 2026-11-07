using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace R3d.Updater;

/// <summary>Chooses the right release asset for this machine and finds its checksum.</summary>
internal static class AssetSelector
{
    private static readonly string[] ArchiveExtensions = [".zip", ".tar.gz", ".tgz"];

    /// <summary>"win-x64", "linux-arm64", "osx-arm64", ...</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    public static bool IsArchive(string name) =>
        ArchiveExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static GitHubAsset? PickArchive(IReadOnlyList<GitHubAsset> assets, string version, string rid, string? pattern)
    {
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var expanded = pattern.Replace("{version}", version).Replace("{rid}", rid);
            var rx = new Regex("^" + Regex.Escape(expanded).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            return assets.FirstOrDefault(a => rx.IsMatch(a.Name));
        }

        var archives = assets.Where(a => IsArchive(a.Name)).ToList();
        var matching = archives.Where(a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count > 0)
        {
            // Prefer the archive type native to the OS when both exist.
            var preferred = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            return matching.FirstOrDefault(a => a.Name.EndsWith(preferred, StringComparison.OrdinalIgnoreCase)) ?? matching[0];
        }

        // Single-platform release that doesn't name its platform: accept the only archive.
        var anyRid = archives.Any(a => Regex.IsMatch(a.Name, @"(win|linux|osx|macos)[-_]", RegexOptions.IgnoreCase));
        return !anyRid && archives.Count == 1 ? archives[0] : null;
    }

    public static GitHubAsset? PickChecksumFile(IReadOnlyList<GitHubAsset> assets) =>
        assets.FirstOrDefault(a => a.Name.Contains("SHA256SUMS", StringComparison.OrdinalIgnoreCase)
                                   || a.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads "hash  filename" lines (sha256sum format) and returns the hash for <paramref name="fileName"/>.</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0) continue;
            var hash = line[..space];
            var name = line[(space + 1)..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && hash.Length == 64) return hash.ToLowerInvariant();
        }
        return null;
    }
}
