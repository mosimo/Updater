namespace R3d.Updater;

public enum UpdateTransport
{
    /// <summary>Try git over SSH first; if that fails and a GitHub token is available, use the API.</summary>
    Auto,
    /// <summary>Only git over SSH (needs git on PATH and an SSH key with access to the repo).</summary>
    GitSsh,
    /// <summary>Only the GitHub REST API (needs a token for private repos).</summary>
    GitHubApi,
}

/// <summary>Everything an app configures to get self-updating. Only <see cref="Repository"/> is required.</summary>
public sealed class UpdaterOptions
{
    /// <summary>GitHub repository as "owner/name", e.g. "mosimo/R3dMqtt".</summary>
    public required string Repository { get; init; }

    /// <summary>Short name used for the per-app state folder. Defaults to the entry assembly name.</summary>
    public string? AppName { get; init; }

    /// <summary>The running version. Defaults to the entry assembly's informational version (build metadata stripped).</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>How releases are fetched. Default <see cref="UpdateTransport.Auto"/>: git over the user's SSH key, falling back to the GitHub API.</summary>
    public UpdateTransport Transport { get; init; } = UpdateTransport.Auto;

    /// <summary>Git URL for the SSH transport. Defaults to <c>git@github.com:{Repository}.git</c>.</summary>
    public string? GitUrl { get; init; }

    /// <summary>Branch the release workflow publishes packages to for the SSH transport. Default "release-assets".</summary>
    public string AssetsBranch { get; init; } = "release-assets";

    /// <summary>GitHub token with read access to the repo's releases. When null, <see cref="TokenResolver"/> looks in the environment, a token file and the gh CLI.</summary>
    public string? Token { get; init; }

    /// <summary>Offer pre-releases as well as normal releases.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>
    /// Optional glob for the release asset to install; "{version}" and "{rid}" are substituted
    /// (e.g. "MyApp-{version}-{rid}.zip"). By default the asset is picked by OS and architecture.
    /// </summary>
    public string? AssetPattern { get; init; }

    /// <summary>Folder the app lives in. Defaults to the folder of the running executable.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Where downloads and updater state go. Defaults to a per-app folder under the user's local app data.</summary>
    public string? StateDirectory { get; init; }

    /// <summary>How often the background loop checks. Default 6 hours.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Wait before the first background check, so startup isn't slowed. Default 10 seconds.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Download new versions as soon as they are found (default). If false, only <c>UpdateAvailable</c> is raised.</summary>
    public bool AutoDownload { get; init; } = true;

    /// <summary>Refuse to install when the release has no SHA256 checksum for the asset. Default false (verified whenever a checksum is published).</summary>
    public bool RequireChecksum { get; init; }

    /// <summary>Install-relative paths (files or folders) the update must never overwrite, e.g. a user-editable config next to the exe.</summary>
    public IReadOnlyList<string> PreservePaths { get; init; } = [];

    /// <summary>Called when the app must quit after the new version was launched. Default: <c>Environment.Exit(0)</c>. UI apps should pass their graceful shutdown.</summary>
    public Action? ExitAction { get; init; }

    /// <summary>Receives diagnostic messages.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>GitHub API root; only changed for tests or GitHub Enterprise.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com/";
}
