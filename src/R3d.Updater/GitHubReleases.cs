using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace R3d.Updater;

public sealed class GitHubAsset
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Thin GitHub Releases REST client. Works with private repos when given a token.</summary>
internal sealed class GitHubReleaseClient(HttpClient http, string apiBase, string repo, string? token) : IReleaseSource
{
    private readonly Uri _base = new(apiBase.EndsWith('/') ? apiBase : apiBase + "/");

    private HttpRequestMessage Request(string relative, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, relative));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("R3d-Updater", "1"));
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        // Set per request: HttpClient drops it when GitHub redirects the download to its storage host.
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(string relative, string accept, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(Request(relative, accept), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Could not reach GitHub: {ex.Message}", ex);
        }
        if (resp.IsSuccessStatusCode) return resp;

        var status = resp.StatusCode;
        resp.Dispose();
        var hint = status switch
        {
            HttpStatusCode.NotFound when string.IsNullOrEmpty(token) =>
                " (repo not found or private - set a GitHub token, see the Updater README)",
            HttpStatusCode.NotFound => " (repo not found, or the token cannot read it)",
            HttpStatusCode.Unauthorized => " (the GitHub token is invalid or expired)",
            HttpStatusCode.Forbidden => " (rate limited, or the token lacks access)",
            _ => "",
        };
        throw new UpdateException($"GitHub returned {(int)status} for {repo}{hint}");
    }

    /// <summary>The newest non-draft release, or null when the repo has none.</summary>
    public async Task<GitHubRelease?> GetLatestAsync(bool includePrereleases, CancellationToken ct)
    {
        if (!includePrereleases)
        {
            try
            {
                using var resp = await SendAsync($"repos/{repo}/releases/latest", "application/vnd.github+json", ct).ConfigureAwait(false);
                return await ReadJsonAsync<GitHubRelease>(resp, ct).ConfigureAwait(false);
            }
            catch (UpdateException ex) when (ex.Message.Contains("returned 404") && !string.IsNullOrEmpty(token))
            {
                return null; // authenticated and still 404: no published release yet
            }
        }

        using var list = await SendAsync($"repos/{repo}/releases?per_page=30", "application/vnd.github+json", ct).ConfigureAwait(false);
        var releases = await ReadJsonAsync<List<GitHubRelease>>(list, ct).ConfigureAwait(false) ?? [];
        return releases
            .Where(r => !r.Draft && SemVer.TryParse(r.TagName, out _))
            .OrderByDescending(r => SemVer.Parse(r.TagName))
            .FirstOrDefault();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new UpdateException("GitHub sent an unreadable response.", ex);
        }
    }

    public async Task<string> GetAssetTextAsync(GitHubAsset asset, CancellationToken ct)
    {
        using var resp = await SendAsync($"repos/{repo}/releases/assets/{asset.Id}", "application/octet-stream", ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadAssetAsync(GitHubAsset asset, Stream destination, Action<long, long>? progress, CancellationToken ct)
    {
        using var resp = await SendAsync($"repos/{repo}/releases/assets/{asset.Id}", "application/octet-stream", ct).ConfigureAwait(false);
        var total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            progress?.Invoke(done, total);
        }
        if (total > 0 && done != total) throw new UpdateException($"Download of {asset.Name} was cut short ({done} of {total} bytes).");
    }
}
