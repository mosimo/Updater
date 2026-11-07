using System.Diagnostics;

namespace R3d.Updater;

/// <summary>
/// Finds a GitHub token for private repos. Order: explicit option, env vars
/// (<c>UPDATER_GITHUB_TOKEN</c>, <c>GITHUB_TOKEN</c>, <c>GH_TOKEN</c>), the file
/// <c>&lt;local app data&gt;/R3dUpdater/token</c>, then <c>gh auth token</c> if the GitHub CLI is installed.
/// </summary>
public static class TokenResolver
{
    public static string TokenFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R3dUpdater", "token");

    public static string? Resolve(string? explicitToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();

        foreach (var name in new[] { "UPDATER_GITHUB_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        try
        {
            if (File.Exists(TokenFilePath))
            {
                var v = File.ReadAllText(TokenFilePath).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return FromGhCli();
    }

    private static string? FromGhCli()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // gh not installed
        }
    }
}
