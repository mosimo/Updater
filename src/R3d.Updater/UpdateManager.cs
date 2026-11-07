using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace R3d.Updater;

/// <summary>A newer release that was found on GitHub.</summary>
public sealed class UpdateInfo
{
    public required SemVer Version { get; init; }
    public required string Tag { get; init; }
    /// <summary>Release notes (Markdown) as written on the GitHub release.</summary>
    public string? Notes { get; init; }
    public string? Url { get; init; }
    public long DownloadSize { get; init; }
    internal GitHubAsset Asset { get; init; } = null!;
    internal GitHubAsset? ChecksumAsset { get; init; }
}

/// <summary>An update that is downloaded, verified and unpacked, waiting to be installed.</summary>
public sealed class StagedUpdate
{
    public required SemVer Version { get; init; }
    public required string Tag { get; init; }
    public required string PayloadDirectory { get; init; }
}

public sealed class DownloadProgressEventArgs(UpdateInfo update, long received, long total) : EventArgs
{
    public UpdateInfo Update { get; } = update;
    public long BytesReceived { get; } = received;
    public long TotalBytes { get; } = total;
    public double Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : 0;
}

public sealed class UpdateErrorEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

/// <summary>
/// Self-updater for an app released on GitHub. Create one, call <see cref="Start"/>, handle <see cref="UpdateReady"/>.
/// Events are raised on the <see cref="SynchronizationContext"/> that was current when the manager was created
/// (the UI thread in Avalonia/WPF/WinForms apps if you create it there), otherwise on a background thread.
/// </summary>
public sealed class UpdateManager : IDisposable
{
    internal const string WaitPidVariable = "R3D_UPDATER_WAIT_PID";

    private readonly UpdaterOptions _options;
    private readonly Paths _paths;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GitHubReleaseClient? _github;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UpdateManager(UpdaterOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _paths = new Paths(options);
        CurrentVersion = _paths.CurrentVersion;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>The version of the running app, as used for comparisons.</summary>
    public SemVer CurrentVersion { get; }

    /// <summary>False when the app runs under the dotnet host (e.g. <c>dotnet run</c>), where installing is refused. Skip <see cref="Start"/> then.</summary>
    public bool CanSelfInstall => _paths.CanSelfInstall;

    /// <summary>The newest newer release found so far, or null.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>The downloaded update waiting to be installed, or null.</summary>
    public StagedUpdate? Staged { get; private set; }

    /// <summary>A newer version exists on GitHub (raised once per version).</summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;
    /// <summary>Download progress for the current update.</summary>
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;
    /// <summary>An update is downloaded and verified; call <see cref="ApplyAndRestart"/> (now, or when convenient for the user).</summary>
    public event EventHandler<StagedUpdate>? UpdateReady;
    /// <summary>A background check or download failed. The loop keeps going.</summary>
    public event EventHandler<UpdateErrorEventArgs>? Error;

    // ---- Startup hook -------------------------------------------------------------------

    /// <summary>
    /// Call first thing in <c>Main</c>. Waits for the previous instance to exit after a restart, removes files left over from the
    /// last update, and (by default) installs an update that was downloaded earlier but never applied.
    /// Returns true when it installed an update and started the new version: the caller should return immediately.
    /// </summary>
    public static bool OnStartup(UpdaterOptions options, bool applyPendingUpdate = true)
    {
        var paths = new Paths(options);

        if (int.TryParse(Environment.GetEnvironmentVariable(WaitPidVariable), out var pid))
        {
            Environment.SetEnvironmentVariable(WaitPidVariable, null);
            try { Process.GetProcessById(pid).WaitForExit(15000); }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
        }

        Installer.Cleanup(paths.InstallDirectory);

        if (!applyPendingUpdate || !paths.CanSelfInstall) return false;
        var staged = LoadStaged(paths);
        if (staged is null) return false;
        if (staged.Version <= paths.CurrentVersion)
        {
            DiscardStaged(paths);
            return false;
        }

        try
        {
            options.Log?.Invoke($"Installing pending update {staged.Version}");
            InstallStaged(paths, options, staged);
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"Pending update failed: {ex.Message}");
            DiscardStaged(paths);
            return false;
        }
        Restart(options, exitProcess: false);
        return true;
    }

    // ---- Background loop ----------------------------------------------------------------

    /// <summary>Starts the background loop: first check after <see cref="UpdaterOptions.InitialDelay"/>, then every <see cref="UpdaterOptions.CheckInterval"/>.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var pending = LoadStaged(_paths);
        if (pending is not null && pending.Version > CurrentVersion)
        {
            Staged = pending;
            Raise(() => UpdateReady?.Invoke(this, pending));
        }

        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.InitialDelay, ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var update = await CheckAsync(ct).ConfigureAwait(false);
                        if (update is not null && _options.AutoDownload && Staged?.Version != update.Version)
                            await DownloadAsync(update, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        Log($"Update check failed: {ex.Message}");
                        Raise(() => Error?.Invoke(this, new UpdateErrorEventArgs(ex)));
                    }
                    await Task.Delay(_options.CheckInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    /// <summary>Stops the background loop.</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { } // stopped before the loop body ever ran
        cts.Dispose();
    }

    // ---- Check / download / apply -------------------------------------------------------

    /// <summary>Asks GitHub for the latest release. Returns it when it is newer than the running version, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gh = await GetClientAsync().ConfigureAwait(false);
            var release = await gh.GetLatestAsync(_options.IncludePrereleases, ct).ConfigureAwait(false);
            if (release is null || release.Draft) return null;
            if (!SemVer.TryParse(release.TagName, out var version))
                throw new UpdateException($"Release tag '{release.TagName}' is not a version number.");
            if (!_options.IncludePrereleases && version.IsPrerelease) return null;

            var state = State.Load(_paths);
            if (version <= CurrentVersion || version.ToString() == state.LastInstalled)
            {
                Log($"Up to date ({CurrentVersion}; latest release {version})");
                return null;
            }

            var rid = AssetSelector.CurrentRid();
            var asset = AssetSelector.PickArchive(release.Assets, version.ToString(), rid, _options.AssetPattern)
                        ?? throw new UpdateException($"Release {release.TagName} has no {rid} package.");
            var info = new UpdateInfo
            {
                Version = version,
                Tag = release.TagName,
                Notes = release.Body,
                Url = release.HtmlUrl,
                DownloadSize = asset.Size,
                Asset = asset,
                ChecksumAsset = AssetSelector.PickChecksumFile(release.Assets),
            };

            var isNew = Available?.Version != version;
            Available = info;
            Log($"Update available: {version} (running {CurrentVersion})");
            if (isNew) Raise(() => UpdateAvailable?.Invoke(this, info));
            return info;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Downloads, verifies and unpacks the update. Raises <see cref="UpdateReady"/> when done.</summary>
    public async Task<StagedUpdate> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Staged is { } already && already.Version == update.Version && Directory.Exists(already.PayloadDirectory))
                return already;

            var gh = await GetClientAsync().ConfigureAwait(false);
            var downloads = Path.Combine(_paths.StateDirectory, "downloads");
            Directory.CreateDirectory(downloads);
            var archive = Path.Combine(downloads, update.Asset.Name);
            var partial = archive + ".partial";

            try
            {
                Log($"Downloading {update.Asset.Name} ({update.DownloadSize} bytes)");
                await using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await gh.DownloadAssetAsync(update.Asset, fs,
                        (done, total) => Raise(() => DownloadProgress?.Invoke(this, new DownloadProgressEventArgs(update, done, total))),
                        ct).ConfigureAwait(false);
                }
                await VerifyChecksumAsync(gh, update, partial, ct).ConfigureAwait(false);
                File.Move(partial, archive, overwrite: true);

                DiscardStaged(_paths);
                var dir = Path.Combine(_paths.StateDirectory, "staged", update.Version.ToString());
                var extracted = Path.Combine(dir, "extracted");
                Installer.Extract(archive, extracted);
                var payload = Installer.PayloadRoot(extracted);
                File.WriteAllText(Path.Combine(_paths.StateDirectory, "staged.json"),
                    JsonSerializer.Serialize(new StagedFile(update.Version.ToString(), update.Tag, payload)));

                var staged = new StagedUpdate { Version = update.Version, Tag = update.Tag, PayloadDirectory = payload };
                Staged = staged;
                Log($"Update {update.Version} is ready to install");
                Raise(() => UpdateReady?.Invoke(this, staged));
                return staged;
            }
            finally
            {
                TryDelete(partial);
                TryDelete(archive);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Check, download and apply in one go. Returns false when already up to date.</summary>
    public async Task<bool> UpdateNowAsync(CancellationToken ct = default)
    {
        var update = await CheckAsync(ct).ConfigureAwait(false);
        if (update is null) return false;
        await DownloadAsync(update, ct).ConfigureAwait(false);
        ApplyAndRestart();
        return true;
    }

    /// <summary>Installs the staged update into the app folder without restarting (it takes effect on the next start).</summary>
    public void Apply()
    {
        var staged = Staged ?? LoadStaged(_paths) ?? throw new InvalidOperationException("No update has been downloaded.");
        InstallStaged(_paths, _options, staged);
        Staged = null;
        Available = null;
    }

    /// <summary>Installs the staged update, starts the new version with the same arguments and exits this process.</summary>
    public void ApplyAndRestart()
    {
        Apply();
        Restart(_options, exitProcess: true);
    }

    /// <summary>Don't offer this version again (it is still offered when a newer one appears).</summary>
    public void SkipVersion(UpdateInfo update)
    {
        var s = State.Load(_paths);
        s.LastInstalled = update.Version.ToString();
        s.Save(_paths);
        if (Available?.Version == update.Version) Available = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }

    // ---- Internals ----------------------------------------------------------------------

    private async Task<GitHubReleaseClient> GetClientAsync()
    {
        if (_github is not null) return _github;
        // The gh CLI fallback blocks on a process, so keep it off the caller's thread.
        var token = await Task.Run(() => TokenResolver.Resolve(_options.Token)).ConfigureAwait(false);
        if (token is null) Log("No GitHub token found; private repositories will not be reachable");
        return _github = new GitHubReleaseClient(_http, _options.ApiBaseUrl, _options.Repository, token);
    }

    private async Task VerifyChecksumAsync(GitHubReleaseClient gh, UpdateInfo update, string file, CancellationToken ct)
    {
        string? expected = null;
        if (update.ChecksumAsset is not null)
            expected = AssetSelector.FindChecksum(await gh.GetAssetTextAsync(update.ChecksumAsset, ct).ConfigureAwait(false), update.Asset.Name);

        if (expected is null)
        {
            if (_options.RequireChecksum) throw new UpdateException($"No SHA256 checksum is published for {update.Asset.Name}.");
            Log("No checksum published; skipping verification");
            return;
        }

        await using var fs = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected) throw new UpdateException($"Checksum mismatch for {update.Asset.Name}; the download was discarded.");
        Log("Checksum verified");
    }

    private static void InstallStaged(Paths paths, UpdaterOptions options, StagedUpdate staged)
    {
        if (!paths.CanSelfInstall)
            throw new InvalidOperationException(
                "Refusing to install: the app is running under the 'dotnet' host (e.g. dotnet run), so the install folder is the .NET SDK. " +
                "Set UpdaterOptions.InstallDirectory to override.");
        if (!Directory.Exists(staged.PayloadDirectory))
            throw new UpdateException("The downloaded update is no longer on disk.");

        Installer.Install(staged.PayloadDirectory, paths.InstallDirectory, options.PreservePaths, options.Log);
        var s = State.Load(paths);
        s.LastInstalled = staged.Version.ToString();
        s.Save(paths);
        DiscardStaged(paths);
    }

    private static void Restart(UpdaterOptions options, bool exitProcess)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable to restart.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
        // The new process waits for this one to exit (see OnStartup), so single-instance locks don't collide.
        psi.Environment[WaitPidVariable] = Environment.ProcessId.ToString();
        Process.Start(psi);

        if (!exitProcess) return;
        if (options.ExitAction is { } exit) exit();
        else Environment.Exit(0);
    }

    private static StagedUpdate? LoadStaged(Paths paths)
    {
        try
        {
            var file = Path.Combine(paths.StateDirectory, "staged.json");
            if (!File.Exists(file)) return null;
            var s = JsonSerializer.Deserialize<StagedFile>(File.ReadAllText(file));
            if (s is null || !Directory.Exists(s.Payload) || !SemVer.TryParse(s.Version, out var v)) return null;
            return new StagedUpdate { Version = v, Tag = s.Tag, PayloadDirectory = s.Payload };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void DiscardStaged(Paths paths)
    {
        TryDelete(Path.Combine(paths.StateDirectory, "staged.json"));
        try
        {
            var dir = Path.Combine(paths.StateDirectory, "staged");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal void ReportError(Exception ex)
    {
        Log($"Update failed: {ex.Message}");
        Raise(() => Error?.Invoke(this, new UpdateErrorEventArgs(ex)));
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private void Raise(Action action)
    {
        try
        {
            if (_sync is not null) _sync.Post(_ => action(), null);
            else action();
        }
        catch (Exception ex) { Log($"Event handler threw: {ex.Message}"); }
    }

    private sealed record StagedFile(string Version, string Tag, string Payload);

    private sealed class State
    {
        public string? LastInstalled { get; set; }

        public static State Load(Paths paths)
        {
            try
            {
                var f = Path.Combine(paths.StateDirectory, "state.json");
                return File.Exists(f) ? JsonSerializer.Deserialize<State>(File.ReadAllText(f)) ?? new State() : new State();
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return new State(); }
        }

        public void Save(Paths paths)
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(Path.Combine(paths.StateDirectory, "state.json"), JsonSerializer.Serialize(this));
        }
    }

    /// <summary>Resolved defaults for app name, versions and folders.</summary>
    private sealed class Paths
    {
        public Paths(UpdaterOptions o)
        {
            var entry = Assembly.GetEntryAssembly();
            var appName = o.AppName ?? entry?.GetName().Name ?? o.Repository.Split('/').Last();
            foreach (var c in Path.GetInvalidFileNameChars()) appName = appName.Replace(c, '_');

            StateDirectory = o.StateDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R3dUpdater", appName);
            InstallDirectory = Path.GetFullPath(o.InstallDirectory ?? AppContext.BaseDirectory);
            _installDirOverridden = o.InstallDirectory is not null;

            var raw = o.CurrentVersion
                      ?? entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? entry?.GetName().Version?.ToString();
            CurrentVersion = SemVer.TryParse(raw, out var v) ? v : SemVer.Zero;
        }

        private readonly bool _installDirOverridden;
        public string StateDirectory { get; }
        public string InstallDirectory { get; }
        public SemVer CurrentVersion { get; }

        /// <summary>False when running as "dotnet app.dll": the process path is the dotnet host, not the app.</summary>
        public bool CanSelfInstall =>
            _installDirOverridden
            || !Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
