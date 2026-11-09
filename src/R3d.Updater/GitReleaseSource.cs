using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace R3d.Updater;

/// <summary>Where releases come from. Two implementations: the GitHub REST API (needs a token for private repos) and git over SSH.</summary>
internal interface IReleaseSource
{
    Task<GitHubRelease?> GetLatestAsync(bool includePrereleases, CancellationToken ct);
    Task<string> GetAssetTextAsync(GitHubAsset asset, CancellationToken ct);
    Task DownloadAssetAsync(GitHubAsset asset, Stream destination, Action<long, long>? progress, CancellationToken ct);
}

/// <summary>
/// Reads releases from an orphan git branch (default <c>release-assets</c>) that the release workflow force-pushes:
/// a <c>manifest.json</c> shaped like a GitHub release (tag_name, body, prerelease, assets[name,size]) next to the
/// package files. Uses the user's own git and SSH key, so no token is needed. Checking is cheap: a blobless shallow
/// clone fetches only the manifest; a package is fetched only when it is downloaded.
/// </summary>
internal sealed class GitReleaseSource(string url, string branch, string workDir, Action<string>? log) : IReleaseSource
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    public async Task<GitHubRelease?> GetLatestAsync(bool includePrereleases, CancellationToken ct)
    {
        DeleteDirectory(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(workDir)!);
        await GitAsync(["clone", "--quiet", "--depth", "1", "--filter=blob:none", "--no-checkout", "--single-branch", "--branch", branch, url, workDir],
            null, MetadataTimeout, ct).ConfigureAwait(false);

        var json = await GitAsync(["show", "HEAD:manifest.json"], workDir, MetadataTimeout, ct).ConfigureAwait(false);
        GitHubRelease? release;
        try { release = JsonSerializer.Deserialize<GitHubRelease>(json); }
        catch (JsonException ex) { throw new UpdateException($"The '{branch}' branch has an unreadable manifest.json.", ex); }
        if (release is null || string.IsNullOrEmpty(release.TagName))
            throw new UpdateException($"The '{branch}' branch has no release in its manifest.json.");
        if (release.Draft || (release.Prerelease && !includePrereleases)) return null;
        return release;
    }

    public Task<string> GetAssetTextAsync(GitHubAsset asset, CancellationToken ct) =>
        GitAsync(["show", "HEAD:" + asset.Name], workDir, MetadataTimeout, ct);

    public async Task DownloadAssetAsync(GitHubAsset asset, Stream destination, Action<long, long>? progress, CancellationToken ct)
    {
        // "git show" streams the blob's raw bytes and fetches it from the remote on demand.
        await GitAsync(["show", "HEAD:" + asset.Name], workDir, DownloadTimeout, ct, destination,
            done => progress?.Invoke(done, asset.Size)).ConfigureAwait(false);
    }

    private async Task<string> GitAsync(IReadOnlyList<string> args, string? cwd, TimeSpan timeout, CancellationToken ct,
        Stream? stdoutTo = null, Action<long>? copied = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (cwd is not null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never wait for a password nobody can type
        // Never block on a passphrase prompt either; the user's ssh-agent / unencrypted key is used as for any git command.
        if (Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null && Environment.GetEnvironmentVariable("GIT_SSH") is null)
            psi.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";

        log?.Invoke("git " + string.Join(' ', args.Select(a => a.Contains(url) ? "<repo>" : a)));
        Process process;
        try { process = Process.Start(psi) ?? throw new UpdateException("Could not start git."); }
        catch (Win32Exception ex) { throw new UpdateException("git is not installed (or not on PATH).", ex); }

        using (process)
        {
            var stdout = stdoutTo is null ? process.StandardOutput.ReadToEndAsync(ct) : CopyAsync(process.StandardOutput.BaseStream, stdoutTo, copied, ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new UpdateException($"git timed out after {timeout.TotalSeconds:0}s.");
            }
            var output = await stdout.ConfigureAwait(false);
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                throw new UpdateException($"git {args[0]} failed: {(error.Length > 0 ? error : "exit code " + process.ExitCode)}");
            return output;
        }
    }

    private static async Task<string> CopyAsync(Stream from, Stream to, Action<long>? copied, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
            copied?.Invoke(total);
        }
        return "";
    }

    /// <summary>Git marks pack files read-only, which makes a plain recursive delete fail on Windows.</summary>
    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
