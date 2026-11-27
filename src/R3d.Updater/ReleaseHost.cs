using System.Text.Json;
using System.Text.Json.Serialization;

namespace R3d.Updater;

public enum ReleaseHostKind
{
    /// <summary>github.com (or GitHub Enterprise through <see cref="ReleaseHost.ApiBaseUrl"/>).</summary>
    GitHub,
    /// <summary>A Forgejo or Gitea server (Gitea-compatible API under <c>/api/v1</c>).</summary>
    Forgejo,
}

/// <summary>
/// One place releases are published. <see cref="UpdaterOptions.Hosts"/> lists them in the order they are tried, e.g.
/// the self-hosted Forgejo first and GitHub (a push mirror) as the fallback.
/// </summary>
public sealed class ReleaseHost
{
    public ReleaseHostKind Kind { get; init; }

    /// <summary>Web address of a Forgejo server, e.g. "https://git.r3dcloud.net". Not used for GitHub.</summary>
    public string? Url { get; init; }

    /// <summary>"owner/name" on this host. Defaults to <see cref="UpdaterOptions.Repository"/>.</summary>
    public string? Repository { get; init; }

    /// <summary>REST API root. Defaults to <see cref="UpdaterOptions.ApiBaseUrl"/> for GitHub and <c>{Url}/api/v1/</c> for Forgejo.</summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>Git URL for the SSH transport. Defaults to <c>git@github.com:owner/name.git</c>, or <c>ssh://git@{host}:{SshPort}/owner/name.git</c> for Forgejo.</summary>
    public string? GitUrl { get; init; }

    /// <summary>SSH port of a Forgejo server's git service. Default 22.</summary>
    public int SshPort { get; init; } = 22;

    /// <summary>
    /// API token with read access to the repo. When null: <see cref="UpdaterOptions.Token"/> and <see cref="TokenResolver"/> for GitHub,
    /// <see cref="TokenResolver.ResolveForgejo"/> for Forgejo.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>github.com, using the options' <see cref="UpdaterOptions.Repository"/>, <see cref="UpdaterOptions.GitUrl"/> and <see cref="UpdaterOptions.Token"/>.</summary>
    public static ReleaseHost GitHub() => new() { Kind = ReleaseHostKind.GitHub };

    /// <summary>A Forgejo server, e.g. <c>ReleaseHost.Forgejo("https://git.r3dcloud.net", sshPort: 8722)</c>.</summary>
    public static ReleaseHost Forgejo(string url, int sshPort = 22) => new() { Kind = ReleaseHostKind.Forgejo, Url = url, SshPort = sshPort };

    /// <summary>File name of the optional override, looked up in the app's state folder and then in the shared R3dUpdater folder.</summary>
    public const string OverrideFileName = "hosts.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads a host list such as <c>[{"kind":"forgejo","url":"https://git.r3dcloud.net","sshPort":8722},{"kind":"github"}]</c>.
    /// Returns null when the file is missing; throws <see cref="UpdateException"/> when it is unreadable.
    /// </summary>
    internal static IReadOnlyList<ReleaseHost>? LoadOverride(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var hosts = JsonSerializer.Deserialize<List<ReleaseHost>>(File.ReadAllText(path), Json);
            if (hosts is null || hosts.Count == 0) throw new UpdateException($"{path} lists no release hosts.");
            if (hosts.Any(h => h.Kind == ReleaseHostKind.Forgejo && string.IsNullOrWhiteSpace(h.Url) && string.IsNullOrWhiteSpace(h.ApiBaseUrl)))
                throw new UpdateException($"{path}: every Forgejo host needs a \"url\".");
            return hosts;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Could not read {path}: {ex.Message}", ex);
        }
    }
}

/// <summary>A <see cref="ReleaseHost"/> with every default filled in.</summary>
internal sealed record ResolvedHost(string Name, ReleaseHostKind Kind, string Repository, string ApiBaseUrl, string GitUrl, string? Token)
{
    public static ResolvedHost From(ReleaseHost h, UpdaterOptions o)
    {
        var repo = h.Repository ?? o.Repository;
        if (h.Kind == ReleaseHostKind.GitHub)
            return new("GitHub", h.Kind, repo, h.ApiBaseUrl ?? o.ApiBaseUrl, h.GitUrl ?? o.GitUrl ?? $"git@github.com:{repo}.git", h.Token ?? o.Token);

        var web = h.Url ?? new Uri(new Uri(h.ApiBaseUrl!), "/").ToString();
        var uri = new Uri(web.EndsWith('/') ? web : web + "/");
        var git = h.GitUrl ?? (h.SshPort == 22 ? $"git@{uri.Host}:{repo}.git" : $"ssh://git@{uri.Host}:{h.SshPort}/{repo}.git");
        return new(uri.Host, h.Kind, repo, h.ApiBaseUrl ?? new Uri(uri, "api/v1/").ToString(), git, h.Token);
    }
}
