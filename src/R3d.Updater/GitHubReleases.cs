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
    /// <summary>Direct download link; Forgejo serves assets only from here (its API returns metadata for the asset id).</summary>
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
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

/// <summary>
/// Thin releases REST client for GitHub and Forgejo (whose API is Gitea's: same release JSON under <c>/api/v1</c>).
/// Works with private repos when given a token.
/// </summary>
internal sealed class ReleaseApiClient(HttpClient http, ResolvedHost host, string? token) : IReleaseSource
{
    private readonly Uri _base = new(host.ApiBaseUrl.EndsWith('/') ? host.ApiBaseUrl : host.ApiBaseUrl + "/");
    private readonly string _repo = host.Repository;
    private bool IsForgejo => host.Kind == ReleaseHostKind.Forgejo;

    private HttpRequestMessage Request(Uri uri, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("R3d-Updater", "1"));
        req.Headers.Accept.ParseAdd(accept);
        if (!IsForgejo) req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        // Set per request: HttpClient drops it when GitHub redirects the download to its storage host.
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue(IsForgejo ? "token" : "Bearer", token);
        return req;
    }

    private Task<HttpResponseMessage> SendAsync(string relative, string accept, CancellationToken ct) =>
        SendAsync(new Uri(_base, relative), accept, ct);

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(Request(uri, accept), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Could not reach {host.Name}: {ex.Message}", ex);
        }
        if (resp.IsSuccessStatusCode) return resp;

        var status = resp.StatusCode;
        resp.Dispose();
        var kind = IsForgejo ? "Forgejo" : "GitHub";
        var hint = status switch
        {
            HttpStatusCode.NotFound when string.IsNullOrEmpty(token) =>
                $" (repo not found or private - set a {kind} token, see the Updater README)",
            HttpStatusCode.NotFound => " (repo not found, or the token cannot read it)",
            HttpStatusCode.Unauthorized => $" (the {kind} token is invalid or expired)",
            HttpStatusCode.Forbidden => " (rate limited, or the token lacks access)",
            _ => "",
        };
        throw new UpdateException($"{host.Name} returned {(int)status} for {_repo}{hint}");
    }

    /// <summary>The newest non-draft release, or null when the repo has none.</summary>
    public async Task<GitHubRelease?> GetLatestAsync(bool includePrereleases, CancellationToken ct)
    {
        if (!includePrereleases)
        {
            try
            {
                using var resp = await SendAsync($"repos/{_repo}/releases/latest", "application/vnd.github+json", ct).ConfigureAwait(false);
                return await ReadJsonAsync<GitHubRelease>(resp, ct).ConfigureAwait(false);
            }
            catch (UpdateException ex) when (ex.Message.Contains("returned 404") && !string.IsNullOrEmpty(token))
            {
                return null; // authenticated and still 404: no published release yet
            }
        }

        // Forgejo pages with "limit", GitHub with "per_page".
        using var list = await SendAsync($"repos/{_repo}/releases?{(IsForgejo ? "limit" : "per_page")}=30", "application/vnd.github+json", ct).ConfigureAwait(false);
        var releases = await ReadJsonAsync<List<GitHubRelease>>(list, ct).ConfigureAwait(false) ?? [];
        return releases
            .Where(r => !r.Draft && SemVer.TryParse(r.TagName, out _))
            .OrderByDescending(r => SemVer.Parse(r.TagName))
            .FirstOrDefault();
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new UpdateException($"{host.Name} sent an unreadable response.", ex);
        }
    }

    /// <summary>GitHub streams the file from the API's asset URL; Forgejo only from the asset's download link (token auth works there too).</summary>
    private Task<HttpResponseMessage> SendAssetAsync(GitHubAsset asset, CancellationToken ct)
    {
        if (!IsForgejo) return SendAsync($"repos/{_repo}/releases/assets/{asset.Id}", "application/octet-stream", ct);
        if (string.IsNullOrEmpty(asset.BrowserDownloadUrl)) throw new UpdateException($"{host.Name} gave no download link for {asset.Name}.");
        return SendAsync(new Uri(asset.BrowserDownloadUrl), "application/octet-stream", ct);
    }

    public async Task<string> GetAssetTextAsync(GitHubAsset asset, CancellationToken ct)
    {
        using var resp = await SendAssetAsync(asset, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadAssetAsync(GitHubAsset asset, Stream destination, Action<long, long>? progress, CancellationToken ct)
    {
        using var resp = await SendAssetAsync(asset, ct).ConfigureAwait(false);
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
