using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace R3d.Updater.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("v0.6.6", "0.6.10")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.1.2+abc123", "1.2.0")]
    public void Orders(string lower, string higher) => Assert.True(SemVer.Parse(lower) < SemVer.Parse(higher));

    [Fact] public void BuildMetadataIgnored() => Assert.Equal(SemVer.Parse("1.2.3+aaa"), SemVer.Parse("v1.2.3+bbb"));
    [Fact] public void ThreeAndFourPartsEqual() => Assert.Equal(SemVer.Parse("1.2.3"), SemVer.Parse("1.2.3.0"));
    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("1.2.3-")]
    public void Rejects(string s) => Assert.False(SemVer.TryParse(s, out _));
}

public class AssetSelectorTests
{
    private static GitHubAsset A(string n) => new() { Name = n };

    [Fact]
    public void PicksPlatformArchive()
    {
        var assets = new[] { A("SHA256SUMS.txt"), A("App-1.0.0-linux-x64.tar.gz"), A("App-1.0.0-win-x64.zip") };
        Assert.Equal("App-1.0.0-win-x64.zip", AssetSelector.PickArchive(assets, "1.0.0", "win-x64", null)!.Name);
        Assert.Equal("App-1.0.0-linux-x64.tar.gz", AssetSelector.PickArchive(assets, "1.0.0", "linux-x64", null)!.Name);
        Assert.Null(AssetSelector.PickArchive(assets, "1.0.0", "osx-arm64", null));
    }

    [Fact]
    public void PatternOverrides()
    {
        var assets = new[] { A("a.zip"), A("Special-1.0.0-win-x64.zip") };
        Assert.Equal("Special-1.0.0-win-x64.zip", AssetSelector.PickArchive(assets, "1.0.0", "win-x64", "Special-{version}-{rid}.zip")!.Name);
    }

    [Fact]
    public void SingleUnnamedArchiveAccepted() =>
        Assert.Equal("app.zip", AssetSelector.PickArchive([A("app.zip"), A("notes.txt")], "1.0.0", "win-x64", null)!.Name);

    [Fact]
    public void ChecksumLookup()
    {
        var hash = new string('a', 64);
        var sums = $"{hash}  App-1.0.0-win-x64.zip\n{new string('b', 64)} *other.zip\n";
        Assert.Equal(hash, AssetSelector.FindChecksum(sums, "App-1.0.0-win-x64.zip"));
        Assert.Equal(new string('b', 64), AssetSelector.FindChecksum(sums, "other.zip"));
        Assert.Null(AssetSelector.FindChecksum(sums, "missing.zip"));
    }
}

public class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r3dupd-" + Guid.NewGuid().ToString("N"));
    public InstallerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void SwapsFilesPreservesAndCleansUp()
    {
        var payload = Path.Combine(_root, "payload");
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(payload, "Fonts"));
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(payload, "app.exe"), "new");
        File.WriteAllText(Path.Combine(payload, "Fonts", "f.ttf"), "font");
        File.WriteAllText(Path.Combine(payload, "settings.json"), "default");
        File.WriteAllText(Path.Combine(install, "app.exe"), "old");
        File.WriteAllText(Path.Combine(install, "settings.json"), "mine");

        Installer.Install(payload, install, ["settings.json"], null);

        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.exe")));
        Assert.Equal("font", File.ReadAllText(Path.Combine(install, "Fonts", "f.ttf")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "settings.json")));
        Assert.True(File.Exists(Path.Combine(install, "app.exe" + Installer.OldSuffix)));

        Installer.Cleanup(install);
        Assert.False(File.Exists(Path.Combine(install, "app.exe" + Installer.OldSuffix)));
    }

    [Fact]
    public void RollsBackWhenASwapFails()
    {
        var payload = Path.Combine(_root, "payload");
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(payload, "a.dll"), "new-a");
        File.WriteAllText(Path.Combine(payload, "b.dll"), "new-b");
        File.WriteAllText(Path.Combine(install, "a.dll"), "old-a");
        File.WriteAllText(Path.Combine(install, "b.dll"), "old-b");
        // A directory squatting on b's .r3dold name makes that rename fail after a.dll was already swapped.
        Directory.CreateDirectory(Path.Combine(install, "b.dll" + Installer.OldSuffix));
        File.WriteAllText(Path.Combine(install, "b.dll" + Installer.OldSuffix, "x"), "x");

        Assert.ThrowsAny<Exception>(() => Installer.Install(payload, install, [], null));

        Assert.Equal("old-a", File.ReadAllText(Path.Combine(install, "a.dll")));
        Assert.Equal("old-b", File.ReadAllText(Path.Combine(install, "b.dll")));
        Assert.False(File.Exists(Path.Combine(install, "a.dll" + Installer.NewSuffix)));
    }

    [Fact]
    public void FlattensSingleRootFolder()
    {
        var inner = Path.Combine(_root, "x", "App-1.0-win-x64");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "app.exe"), "x");
        Assert.Equal(inner, Installer.PayloadRoot(Path.Combine(_root, "x")));
    }
}

/// <summary>Drives the whole flow against a fake GitHub API.</summary>
public class UpdateManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r3dupd-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _state;

    public UpdateManagerTests()
    {
        _install = Path.Combine(_root, "install");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "app.exe"), "v1");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Rid => AssetSelector.CurrentRid();

    internal static byte[] MakeZip(string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add($"App-{version}-{Rid}/app.exe", "v" + version);
            Add($"App-{version}-{Rid}/Fonts/a.ttf", "font");
        }
        return ms.ToArray();
    }

    internal sealed class FakeGitHub(string tag, byte[] zip, bool badChecksum = false, bool withChecksum = true) : HttpMessageHandler
    {
        public string? LastAuth;
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastAuth = request.Headers.Authorization?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            var version = tag.TrimStart('v');
            var zipName = $"App-{version}-{Rid}.zip";
            var hash = badChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            var sums = $"{hash}  {zipName}\n";

            if (path.EndsWith("/releases/latest"))
            {
                var assets = new List<object> { new { id = 1, name = zipName, size = zip.Length } };
                if (withChecksum) assets.Add(new { id = 2, name = "SHA256SUMS.txt", size = sums.Length });
                var json = JsonSerializer.Serialize(new { tag_name = tag, body = "notes", draft = false, prerelease = false, assets });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            if (path.EndsWith("/assets/1")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            if (path.EndsWith("/assets/2")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private UpdateManager Manager(FakeGitHub fake, string current = "1.0.0", bool requireChecksum = false) =>
        new(new UpdaterOptions
        {
            Repository = "me/App", Transport = UpdateTransport.GitHubApi,
            AppName = "App",
            CurrentVersion = current,
            Token = "tkn",
            InstallDirectory = _install,
            StateDirectory = _state,
            RequireChecksum = requireChecksum,
        }, new HttpClient(fake));

    [Fact]
    public async Task FindsDownloadsAndInstallsNewRelease()
    {
        var fake = new FakeGitHub("v1.1.0", MakeZip("1.1.0"));
        using var m = Manager(fake);
        UpdateInfo? seen = null;
        StagedUpdate? ready = null;
        m.UpdateAvailable += (_, u) => seen = u;
        m.UpdateReady += (_, s) => ready = s;

        var info = await m.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal("1.1.0", info!.Version.ToString());
        Assert.Equal("notes", info.Notes);
        Assert.Equal("Bearer tkn", fake.LastAuth);
        Assert.Same(info, seen);

        var staged = await m.DownloadAsync(info);
        Assert.Same(staged, ready);

        m.Apply();
        Assert.Equal("v1.1.0", File.ReadAllText(Path.Combine(_install, "app.exe")));
        Assert.Equal("font", File.ReadAllText(Path.Combine(_install, "Fonts", "a.ttf")));
        Assert.Null(m.Staged);
        Assert.Empty(Directory.GetFiles(Path.Combine(_state, "downloads")));
    }

    [Fact]
    public async Task NoUpdateWhenCurrent()
    {
        using var m = Manager(new FakeGitHub("v1.0.0", MakeZip("1.0.0")));
        Assert.Null(await m.CheckAsync());
    }

    [Fact]
    public async Task NoUpdateWhenAheadOfRelease()
    {
        using var m = Manager(new FakeGitHub("v1.0.0", MakeZip("1.0.0")), current: "1.2.0+abc");
        Assert.Null(await m.CheckAsync());
    }

    [Fact]
    public async Task ChecksumMismatchIsRejectedAndNothingInstalled()
    {
        using var m = Manager(new FakeGitHub("v1.1.0", MakeZip("1.1.0"), badChecksum: true));
        var info = (await m.CheckAsync())!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => m.DownloadAsync(info));
        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_install, "app.exe")));
        Assert.Null(m.Staged);
    }

    [Fact]
    public async Task RequireChecksumRejectsReleaseWithoutOne()
    {
        using var m = Manager(new FakeGitHub("v1.1.0", MakeZip("1.1.0"), withChecksum: false), requireChecksum: true);
        var info = (await m.CheckAsync())!;
        await Assert.ThrowsAsync<UpdateException>(() => m.DownloadAsync(info));
    }

    [Fact]
    public async Task StagedUpdateSurvivesRestartAndInstallsOnStartup()
    {
        var fake = new FakeGitHub("v1.1.0", MakeZip("1.1.0"));
        using (var m = Manager(fake))
            await m.DownloadAsync((await m.CheckAsync())!);

        // A later run: OnStartup picks up the staged update. (It would then relaunch the app, so only test the loader via Start.)
        using var next = Manager(fake);
        StagedUpdate? ready = null;
        next.UpdateReady += (_, s) => ready = s;
        next.Start();
        await next.StopAsync();
        Assert.Equal("1.1.0", ready?.Version.ToString());
    }

    [Fact]
    public async Task BackgroundLoopDownloadsAutomatically()
    {
        var fake = new FakeGitHub("v2.0.0", MakeZip("2.0.0"));
        var tcs = new TaskCompletionSource<StagedUpdate>();
        using var m = new UpdateManager(new UpdaterOptions
        {
            Repository = "me/App", Transport = UpdateTransport.GitHubApi, AppName = "App", CurrentVersion = "1.0.0", Token = "t",
            InstallDirectory = _install, StateDirectory = _state, InitialDelay = TimeSpan.FromMilliseconds(10),
        }, new HttpClient(fake));
        m.UpdateReady += (_, s) => tcs.TrySetResult(s);
        m.Start();
        var staged = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await m.StopAsync();
        Assert.Equal("2.0.0", staged.Version.ToString());
    }

    [Fact]
    public async Task ApplyRefusedUnderDotnetHost()
    {
        // The test host is "dotnet testhost" so, without an explicit InstallDirectory, installing must be refused.
        var fake = new FakeGitHub("v1.1.0", MakeZip("1.1.0"));
        using var m = new UpdateManager(new UpdaterOptions
        {
            Repository = "me/App", Transport = UpdateTransport.GitHubApi, AppName = "App", CurrentVersion = "1.0.0", Token = "t", StateDirectory = _state,
        }, new HttpClient(fake));
        await m.DownloadAsync((await m.CheckAsync())!);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            Assert.Throws<InvalidOperationException>(() => m.Apply());
    }
}

/// <summary>Runs the real git transport against a local repository standing in for GitHub.</summary>
public class GitTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r3dgit-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _state;
    private readonly string _origin;

    public GitTransportTests()
    {
        _install = Path.Combine(_root, "install");
        _state = Path.Combine(_root, "state");
        _origin = Path.Combine(_root, "origin");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "app.exe"), "v1");
    }

    public void Dispose() => GitReleaseSource.DeleteDirectory(_root);

    private static bool Git(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Builds the repo the release workflow would publish: an orphan branch with manifest.json, the package and its checksums.</summary>
    private bool PublishBranch(string tag, byte[]? zipOverride = null)
    {
        var version = tag.TrimStart('v');
        var zip = zipOverride ?? ZipFor(version);
        var zipName = $"App-{version}-{AssetSelector.CurrentRid()}.zip";
        Directory.CreateDirectory(_origin);
        File.WriteAllBytes(Path.Combine(_origin, zipName), zip);
        File.WriteAllText(Path.Combine(_origin, "SHA256SUMS.txt"), $"{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}  {zipName}\n");
        File.WriteAllText(Path.Combine(_origin, "manifest.json"), JsonSerializer.Serialize(new
        {
            tag_name = tag, body = "git notes", draft = false, prerelease = false,
            assets = new object[] { new { name = zipName, size = zip.Length }, new { name = "SHA256SUMS.txt", size = 10 } },
        }));
        return Git(_origin, "init", "-q", "-b", "release-assets")
            && Git(_origin, "config", "uploadpack.allowFilter", "true")
            && Git(_origin, "config", "uploadpack.allowAnySHA1InWant", "true")
            && Git(_origin, "add", "-A")
            && Git(_origin, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "release");
    }

    private static byte[] ZipFor(string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry($"App-{version}/app.exe").Open());
            w.Write("v" + version);
        }
        return ms.ToArray();
    }

    private UpdateManager Manager(UpdateTransport transport = UpdateTransport.GitSsh, string? gitUrl = null) => new(new UpdaterOptions
    {
        Repository = "me/App", AppName = "App", CurrentVersion = "1.0.0", InstallDirectory = _install, StateDirectory = _state,
        Transport = transport, GitUrl = gitUrl ?? new Uri(_origin).AbsoluteUri,
        Token = "unused",
    }, new HttpClient(new FailingHandler()));

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task ChecksDownloadsAndInstallsOverGit()
    {
        if (!PublishBranch("v1.2.0")) return; // no git on this machine: nothing to test
        using var m = Manager();
        var info = await m.CheckAsync();
        Assert.Equal("1.2.0", info?.Version.ToString());
        Assert.Equal("git notes", info!.Notes);

        await m.DownloadAsync(info);
        m.Apply();
        Assert.Equal("v1.2.0", File.ReadAllText(Path.Combine(_install, "app.exe")));
    }

    [Fact]
    public async Task UpToDateOverGit()
    {
        if (!PublishBranch("v1.0.0")) return;
        using var m = Manager();
        Assert.Null(await m.CheckAsync());
    }

    [Fact]
    public async Task CorruptPackageOverGitIsRejected()
    {
        if (!PublishBranch("v1.2.0")) return;
        // Tamper with the package after the checksum was written.
        var zipName = $"App-1.2.0-{AssetSelector.CurrentRid()}.zip";
        File.WriteAllText(Path.Combine(_origin, zipName), "garbage");
        Assert.True(Git(_origin, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qam", "tamper"));
        using var m = Manager();
        var info = (await m.CheckAsync())!;
        await Assert.ThrowsAsync<UpdateException>(() => m.DownloadAsync(info));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_install, "app.exe")));
    }

    [Fact]
    public async Task GitOnlyReportsUnreachableRepo()
    {
        using var m = Manager(gitUrl: new Uri(Path.Combine(_root, "nope")).AbsoluteUri);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => m.CheckAsync());
        Assert.Contains("git", ex.Message);
    }

    [Fact]
    public async Task AutoFallsBackToApiWhenGitFails()
    {
        var fake = new UpdateManagerTests.FakeGitHub("v1.1.0", UpdateManagerTests.MakeZip("1.1.0"));
        using var m = new UpdateManager(new UpdaterOptions
        {
            Repository = "me/App", AppName = "App", CurrentVersion = "1.0.0", InstallDirectory = _install, StateDirectory = _state,
            Transport = UpdateTransport.Auto, GitUrl = new Uri(Path.Combine(_root, "nope")).AbsoluteUri, Token = "tkn",
        }, new HttpClient(fake));
        Assert.Equal("1.1.0", (await m.CheckAsync())?.Version.ToString());
    }
}
