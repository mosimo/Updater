using System.Formats.Tar;
using System.IO.Compression;

namespace R3d.Updater;

/// <summary>Unpacks an update and swaps it into the install folder, with rollback.</summary>
/// <remarks>
/// Running executables can be renamed (not deleted or overwritten) on Windows, so each file is
/// copied next to its target as <c>*.r3dnew</c>, then the old file is renamed to <c>*.r3dold</c> and the new
/// one renamed into place. Leftover <c>*.r3dold</c> files are removed on the next start.
/// </remarks>
internal static class Installer
{
    public const string NewSuffix = ".r3dnew";
    public const string OldSuffix = ".r3dold";

    public static void Extract(string archivePath, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination);
        }
        else
        {
            using var file = File.OpenRead(archivePath);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
        }
    }

    /// <summary>If the archive has a single top-level folder (e.g. "App-1.0-win-x64/"), the payload is that folder.</summary>
    public static string PayloadRoot(string extracted)
    {
        var current = extracted;
        while (true)
        {
            var files = Directory.GetFiles(current);
            var dirs = Directory.GetDirectories(current);
            if (files.Length == 0 && dirs.Length == 1) current = dirs[0];
            else return current;
        }
    }

    public static void Install(string payloadRoot, string installDir, IReadOnlyList<string> preserve, Action<string>? log)
    {
        var preserved = preserve.Select(p => Normalize(p)).ToList();
        var relFiles = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payloadRoot, f))
            .Where(r => !IsPreserved(r, preserved) && !r.EndsWith(NewSuffix, StringComparison.Ordinal))
            .ToList();
        if (relFiles.Count == 0) throw new UpdateException("The update package is empty.");

        var staged = new List<string>();   // *.r3dnew files created
        var swapped = new List<(string target, bool hadOld)>();
        try
        {
            // Phase 1: copy everything next to its target. Nothing live is touched yet.
            foreach (var rel in relFiles)
            {
                var target = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + NewSuffix;
                File.Copy(Path.Combine(payloadRoot, rel), temp, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(Path.Combine(payloadRoot, rel)));
                staged.Add(temp);
            }

            // Phase 2: swap by renaming.
            foreach (var rel in relFiles)
            {
                var target = Path.Combine(installDir, rel);
                var old = target + OldSuffix;
                var hadOld = File.Exists(target);
                if (hadOld)
                {
                    TryDelete(old);
                    File.Move(target, old, overwrite: true);
                }
                swapped.Add((target, hadOld));
                File.Move(target + NewSuffix, target, overwrite: true);
            }
            log?.Invoke($"Installed {relFiles.Count} files into {installDir}");
        }
        catch
        {
            log?.Invoke("Install failed; rolling back");
            foreach (var (target, hadOld) in swapped.AsEnumerable().Reverse())
            {
                try
                {
                    if (hadOld) File.Move(target + OldSuffix, target, overwrite: true);
                    else File.Delete(target);
                }
                catch (Exception ex) { log?.Invoke($"Rollback of {target} failed: {ex.Message}"); }
            }
            foreach (var temp in staged) TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes leftovers from a previous update. Best effort; a file still locked is retried next start.</summary>
    public static void Cleanup(string installDir)
    {
        if (!Directory.Exists(installDir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                if (f.EndsWith(OldSuffix, StringComparison.Ordinal) || f.EndsWith(NewSuffix, StringComparison.Ordinal))
                    TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Normalize(string p) => p.Replace('\\', '/').Trim('/');

    private static bool IsPreserved(string rel, List<string> preserved)
    {
        var r = Normalize(rel);
        return preserved.Any(p => r.Equals(p, StringComparison.OrdinalIgnoreCase)
                                  || r.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
