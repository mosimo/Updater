# R3d.Updater

A small .NET library that lets any app released on GitHub or a self-hosted Forgejo update itself. It checks the repo's latest release in the background, downloads the package for the current OS/architecture, verifies its SHA256, unpacks it, swaps the files into the app folder (with rollback) and restarts the app. Private repos work over the user's own SSH key (no token).

It is repo-agnostic: you pass `owner/name` and the hosts to look on, nothing is hard-coded to a specific app.

## What it expects from a release

- Tag is a version: `v1.2.3` (pre-release suffixes work; pre-releases are ignored unless you opt in).
- An archive asset per platform whose name contains the runtime id: `MyApp-1.2.3-win-x64.zip`, `MyApp-1.2.3-linux-x64.tar.gz` (`.zip`, `.tar.gz`, `.tgz`). If your names differ, set `AssetPattern`, e.g. `"MyApp-{version}-{rid}.zip"`.
- Optional `SHA256SUMS.txt` (`sha256sum` format). If present the download is verified; `RequireChecksum = true` makes it mandatory.
- The archive may have one top-level folder (`MyApp-1.2.3-win-x64/`); it is flattened into the app folder.
- Release notes: `UpdateInfo.Notes` holds only the "What's new" section (the text after a `What's new` heading, up to the first `---` rule), so the install instructions below the rule are not shown in the app. A body that is only the install instructions gives no notes. The full body is in `UpdateInfo.FullNotes`.

All of mosimo/TerminalClient, R3dMqtt and ScpClient already release this way.

## Use it

Reference the project (add this repo as a submodule) or the `R3d.Updater` package from a release:

```bash
git submodule add https://github.com/mosimo/Updater external/Updater
dotnet add src/MyApp reference external/Updater/src/R3d.Updater
```

```csharp
using R3d.Updater;

static class Program
{
    static readonly UpdaterOptions Updates = new()
    {
        Repository = "mosimo/R3dMqtt",
        // Avalonia: shut down gracefully instead of Environment.Exit
        // ExitAction = () => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(),
    };

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. First thing: clean up after the last update, and install one that was downloaded but not yet applied.
        if (UpdateManager.OnStartup(Updates)) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
```

Start the background loop once the UI exists (so events arrive on the UI thread):

```csharp
var updater = new UpdateManager(Updates);          // create on the UI thread
updater.UpdateReady += (_, staged) =>
{
    // show "Version X is ready - restart now?" and, on yes:
    updater.ApplyAndRestart();
    // or ignore it: it installs automatically the next time the app starts.
};
updater.Start();                                   // first check after 10 s, then every 6 h
```

Other entry points: `CheckAsync()`, `DownloadAsync(info)`, `Apply()` (no restart), `UpdateNowAsync()` (a "Check for updates" button), `SkipVersion(info)`, events `UpdateAvailable`, `DownloadProgress`, `Error`. Set `AutoDownload = false` to only be notified.

## Where releases come from: Forgejo first, GitHub as the fallback

`UpdaterOptions.Hosts` lists the places releases are published, in the order they are tried. The first host that has a release wins; a host that cannot be reached (server down, repo not moved yet, no SSH key) is skipped. The default is GitHub only, so apps that don't set it behave as before.

```csharp
static readonly UpdaterOptions Updates = new()
{
    Repository = "mosimo/R3dMqtt",
    Hosts = [ReleaseHost.Forgejo("https://git.r3dcloud.net", sshPort: 8722), ReleaseHost.GitHub()],
};
```

For each host the transport (below) decides the route: git over SSH (`release-assets` branch), then the host's REST API.

| Host | git URL (default) | API |
| --- | --- | --- |
| `ReleaseHost.Forgejo(url, sshPort)` | `ssh://git@<host>:<sshPort>/owner/name.git` (`git@<host>:owner/name.git` on port 22) | `<url>/api/v1/repos/owner/name/releases/latest`; files from each asset's `browser_download_url` |
| `ReleaseHost.GitHub()` | `UpdaterOptions.GitUrl`, else `git@github.com:owner/name.git` | `UpdaterOptions.ApiBaseUrl` (`https://api.github.com/`) |

A `ReleaseHost` can also set its own `Repository`, `GitUrl`, `ApiBaseUrl` and `Token`.

**Changing the hosts without a rebuild:** put a `hosts.json` in the app's state folder (`<local app data>/R3dUpdater/<AppName>/hosts.json`), or in `<local app data>/R3dUpdater/hosts.json` for every app. It replaces the list compiled into the app:

```json
[
  { "kind": "forgejo", "url": "https://git.r3dcloud.net", "sshPort": 8722 },
  { "kind": "github" }
]
```

Fields: `kind` (`forgejo` or `github`), `url`, `sshPort`, `repository`, `gitUrl`, `apiBaseUrl`, `token`. A broken file is reported as an update error instead of being ignored.

**First SSH connection to a new host:** the updater runs git with `BatchMode=yes`, so it cannot answer "trust this host key?". Connect once by hand (`ssh -p 8722 git@git.r3dcloud.net`, or clone any repo from it) so the key is in `known_hosts`; until then the Forgejo git route fails and the next route or host is used.

## Getting releases from a private repo: git over SSH (no token)

SSH access only covers git, not release downloads, so the release workflow also publishes each release to an orphan branch, `release-assets`, which holds `manifest.json` (same shape as a GitHub release: `tag_name`, `body`, `prerelease`, `assets[]`) plus the packages and `SHA256SUMS.txt`. It is force-pushed on every release, so the repo only ever carries the latest one. The updater clones that branch with the user's own `git` and SSH key:

- A check is a blobless shallow clone, so only `manifest.json` is fetched (a few KB).
- The package is fetched only when an update is downloaded.
- Nothing needs configuring beyond what `git clone git@github.com:owner/repo.git` (or the Forgejo SSH URL) already needs: git on PATH and an SSH key (or agent) with access to the repo. Key passphrases can't be prompted for, so use an agent.

Add the step from [docs/release-assets-step.yml](docs/release-assets-step.yml) to the end of the app's release workflow (it expects `dist/`, `notes.md` and `$TAG` as in the apps' existing workflows). It pushes to the server the workflow runs on, so the same step works in GitHub Actions and Forgejo Actions. When Forgejo push-mirrors the repo to GitHub, the branch reaches GitHub too, so apps that only know GitHub keep finding new releases over git.

`UpdaterOptions.Transport` picks the source:

| Value | Behaviour |
| --- | --- |
| `Auto` (default) | per host: git over SSH; if that fails, the host's REST API |
| `GitSsh` | git over SSH only |
| `GitHubApi` | REST API only (GitHub or Forgejo; the name is kept for compatibility) |

For the API route on a private repo a token is needed. GitHub: `ReleaseHost.Token`, `UpdaterOptions.Token`, env `UPDATER_GITHUB_TOKEN` / `GITHUB_TOKEN` / `GH_TOKEN`, `%LOCALAPPDATA%\R3dUpdater\token`, or `gh auth token`. Forgejo: `ReleaseHost.Token`, env `UPDATER_FORGEJO_TOKEN` / `FORGEJO_TOKEN`, or `%LOCALAPPDATA%\R3dUpdater\forgejo-token` (a Forgejo access token with `read:repository`). Neither is needed when the SSH route works.

## How the install works

Files are copied next to their targets as `*.r3dnew`, then each old file is renamed to `*.r3dold` and the new one renamed into place (Windows lets a running exe be renamed, not overwritten). If anything fails everything is rolled back. `*.r3dold` leftovers are removed by `OnStartup` on the next run. Restart launches the same executable with the same arguments; the new process waits for the old one to exit, so single-instance locks don't collide.

Things to know:

- Files that exist in the app folder but not in the update are left alone; use `PreservePaths` for files the update ships but must not overwrite.
- When the app runs as `dotnet app.dll` / `dotnet run`, installing is refused (the process path is the dotnet host) unless `InstallDirectory` is set.
- The install folder must be writable by the user (not `Program Files`).
- User data should live outside the app folder (as the current apps do).
- State (downloads, staged update) is under `%LOCALAPPDATA%\R3dUpdater\<AppName>`.

## Build and test

```bash
dotnet test
dotnet pack src/R3d.Updater -c Release -o artifacts
```

Pushing a tag `vX.Y.Z` builds the package and attaches the `.nupkg` to a release: `.github/workflows/` on GitHub, `.forgejo/workflows/` on Forgejo (Forgejo reads only `.forgejo/workflows/` when it exists, GitHub never reads it).
