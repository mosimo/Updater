using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace R3d.Updater.Tests;

/// <summary>Forgejo releases, and several hosts tried in order (Forgejo first, GitHub as the fallback).</summary>
public class ForgejoTests : IDisposable
{
    private const string Forge = "https://forge.test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r3dforge-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _state;

    public ForgejoTests()
    {
        _install = Path.Combine(_root, "install");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "app.exe"), "v1");
    }

    public void Dispose() => GitReleaseSource.DeleteDirectory(_root);

    /// <summary>A Forgejo server: Gitea's release JSON under /api/v1, files served from each asset's browser_download_url.</summary>
    private sealed class FakeForgejo(string tag, byte[] zip) : HttpMessageHandler
    {
        public readonly List<string> Auth = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Auth.Add(request.Headers.Authorization?.ToString() ?? "");
            var path = request.RequestUri!.AbsolutePath;
            var zipName = $"App-{tag.TrimStart('v')}-{AssetSelector.CurrentRid()}.zip";
            var sums = $"{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}  {zipName}\n";

            if (path == "/api/v1/repos/me/App/releases/latest")
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = tag, body = "forge notes", draft = false, prerelease = false,
                    assets = new object[]
                    {
                        new { id = 7, name = zipName, size = zip.Length, browser_download_url = $"{Forge}/me/App/releases/download/{tag}/{zipName}" },
                        new { id = 8, name = "SHA256SUMS.txt", size = sums.Length, browser_download_url = $"{Forge}/me/App/releases/download/{tag}/SHA256SUMS.txt" },
                    },
                });
                return Ok(new StringContent(json, Encoding.UTF8, "application/json"));
            }
            if (path == $"/me/App/releases/download/{tag}/{zipName}") return Ok(new ByteArrayContent(zip));
            if (path == $"/me/App/releases/download/{tag}/SHA256SUMS.txt") return Ok(new StringContent(sums));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(HttpContent content) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    /// <summary>Sends each request to the fake for its host; a missing fake means the host cannot be reached.</summary>
    private sealed class Router(HttpMessageHandler? forge, HttpMessageHandler? github) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.RequestUri!.Host == "forge.test" ? forge : github;
            if (target is null) throw new HttpRequestException("No such host");
            return new HttpMessageInvoker(target).SendAsync(request, ct);
        }
    }

    private UpdateManager Manager(HttpMessageHandler router, IReadOnlyList<ReleaseHost>? hosts = null) => new(new UpdaterOptions
    {
        Repository = "me/App", AppName = "App", CurrentVersion = "1.0.0", InstallDirectory = _install, StateDirectory = _state,
        Transport = UpdateTransport.GitHubApi, Token = "gh",
        Hosts = hosts ?? [new ReleaseHost { Kind = ReleaseHostKind.Forgejo, Url = Forge, Token = "fj" }, ReleaseHost.GitHub()],
    }, new HttpClient(router));

    [Fact]
    public async Task InstallsFromForgejo()
    {
        var forge = new FakeForgejo("v1.3.0", UpdateManagerTests.MakeZip("1.3.0"));
        using var m = Manager(new Router(forge, null));
        var info = await m.CheckAsync();
        Assert.Equal("1.3.0", info?.Version.ToString());
        Assert.Equal("forge notes", info!.Notes);

        await m.DownloadAsync(info);
        m.Apply();
        Assert.Equal("v1.3.0", File.ReadAllText(Path.Combine(_install, "app.exe")));
        Assert.All(forge.Auth, a => Assert.Equal("token fj", a));
    }

    [Fact]
    public async Task ForgejoIsAskedFirst()
    {
        var forge = new FakeForgejo("v1.3.0", UpdateManagerTests.MakeZip("1.3.0"));
        var github = new UpdateManagerTests.FakeGitHub("v1.9.0", UpdateManagerTests.MakeZip("1.9.0"));
        using var m = Manager(new Router(forge, github));
        Assert.Equal("1.3.0", (await m.CheckAsync())?.Version.ToString());
        Assert.Equal(0, github.Requests);
    }

    [Fact]
    public async Task FallsBackToGitHubWhenForgejoIsDown()
    {
        var github = new UpdateManagerTests.FakeGitHub("v1.1.0", UpdateManagerTests.MakeZip("1.1.0"));
        using var m = Manager(new Router(null, github));
        var info = await m.CheckAsync();
        Assert.Equal("1.1.0", info?.Version.ToString());
        await m.DownloadAsync(info!);
        Assert.Equal("Bearer gh", github.LastAuth);
    }

    [Fact]
    public async Task FallsBackToGitHubWhenRepoIsNotOnForgejoYet()
    {
        var github = new UpdateManagerTests.FakeGitHub("v1.1.0", UpdateManagerTests.MakeZip("1.1.0"));
        var forgeWithoutRepo = new FakeForgejo("v9.9.9", []);
        using var m = Manager(new Router(forgeWithoutRepo, github),
            [new ReleaseHost { Kind = ReleaseHostKind.Forgejo, Url = Forge, Repository = "me/Missing", Token = "fj" }, ReleaseHost.GitHub()]);
        Assert.Equal("1.1.0", (await m.CheckAsync())?.Version.ToString());
    }

    [Fact]
    public async Task AllHostsDownNamesEach()
    {
        using var m = Manager(new Router(null, null));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => m.CheckAsync());
        Assert.Contains("forge.test (API)", ex.Message);
        Assert.Contains("GitHub (API)", ex.Message);
    }

    [Fact]
    public async Task HostsFileReplacesTheBuiltInList()
    {
        var forge = new FakeForgejo("v1.3.0", UpdateManagerTests.MakeZip("1.3.0"));
        var github = new UpdateManagerTests.FakeGitHub("v1.9.0", UpdateManagerTests.MakeZip("1.9.0"));
        Directory.CreateDirectory(_state);
        File.WriteAllText(Path.Combine(_state, ReleaseHost.OverrideFileName), """
            // GitHub only, e.g. while the Forgejo server is down
            [ { "kind": "github" } ]
            """);
        using var m = Manager(new Router(forge, github));
        Assert.Equal("1.9.0", (await m.CheckAsync())?.Version.ToString());
        Assert.Empty(forge.Auth);
    }

    [Fact]
    public async Task BrokenHostsFileIsReported()
    {
        Directory.CreateDirectory(_state);
        File.WriteAllText(Path.Combine(_state, ReleaseHost.OverrideFileName), """[ { "kind": "forgejo" } ]""");
        using var m = Manager(new Router(null, null));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => m.CheckAsync());
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void ForgejoDefaults()
    {
        var options = new UpdaterOptions { Repository = "mosimo/ScpClient" };
        var host = ResolvedHost.From(ReleaseHost.Forgejo("https://git.r3dcloud.net", sshPort: 8722), options);
        Assert.Equal("git.r3dcloud.net", host.Name);
        Assert.Equal("https://git.r3dcloud.net/api/v1/", host.ApiBaseUrl);
        Assert.Equal("ssh://git@git.r3dcloud.net:8722/mosimo/ScpClient.git", host.GitUrl);
        Assert.Equal("git@git.r3dcloud.net:mosimo/ScpClient.git", ResolvedHost.From(ReleaseHost.Forgejo("https://git.r3dcloud.net"), options).GitUrl);
    }

    [Fact]
    public void GitHubKeepsTheOldOptions()
    {
        var options = new UpdaterOptions { Repository = "mosimo/ScpClient", GitUrl = "git@example:x.git", Token = "t" };
        var host = ResolvedHost.From(Assert.Single(options.Hosts), options);
        Assert.Equal(ReleaseHostKind.GitHub, host.Kind);
        Assert.Equal("git@example:x.git", host.GitUrl);
        Assert.Equal("https://api.github.com/", host.ApiBaseUrl);
        Assert.Equal("t", host.Token);
        Assert.Equal("git@github.com:mosimo/ScpClient.git", ResolvedHost.From(ReleaseHost.GitHub(), new UpdaterOptions { Repository = "mosimo/ScpClient" }).GitUrl);
    }
}
