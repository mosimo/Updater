# R3d.Updater

A small .NET library that lets any app released on GitHub update itself. It checks the repo's latest release in the background, downloads the package for the current OS/architecture, verifies its SHA256, unpacks it, swaps the files into the app folder (with rollback) and restarts the app. Private repos work over the user's own SSH key (no token).

It is repo-agnostic: you pass `owner/name`, nothing is hard-coded to a specific app.

## What it expects from a release

- Tag is a version: `v1.2.3` (pre-release suffixes work; pre-releases are ignored unless you opt in).
- An archive asset per platform whose name contains the runtime id: `MyApp-1.2.3-win-x64.zip`, `MyApp-1.2.3-linux-x64.tar.gz` (`.zip`, `.tar.gz`, `.tgz`). If your names differ, set `AssetPattern`, e.g. `"MyApp-{version}-{rid}.zip"`.
- Optional `SHA256SUMS.txt` (`sha256sum` format). If present the download is verified; `RequireChecksum = true` makes it mandatory.
- The archive may have one top-level folder (`MyApp-1.2.3-win-x64/`); it is flattened into the app folder.

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

## Getting releases from a private repo: git over SSH (no token)

GitHub's SSH access only covers git, not release downloads, so the release workflow also publishes each release to an orphan branch, `release-assets`, which holds `manifest.json` (same shape as a GitHub release: `tag_name`, `body`, `prerelease`, `assets[]`) plus the packages and `SHA256SUMS.txt`. It is force-pushed on every release, so the repo only ever carries the latest one. The updater clones that branch with the user's own `git` and SSH key:

- A check is a blobless shallow clone, so only `manifest.json` is fetched (a few KB).
- The package is fetched only when an update is downloaded.
- Nothing needs configuring beyond what `git clone git@github.com:owner/repo.git` already needs: git on PATH and an SSH key (or agent) with access to the repo. Key passphrases can't be prompted for, so use an agent.

Add the step from [docs/release-assets-step.yml](docs/release-assets-step.yml) to the end of the app's release workflow (it expects `dist/`, `notes.md` and `$TAG` as in the apps' existing workflows).

`UpdaterOptions.Transport` picks the source:

| Value | Behaviour |
| --- | --- |
| `Auto` (default) | git over SSH; if that fails and a GitHub token can be found, the GitHub API |
| `GitSsh` | git over SSH only |
| `GitHubApi` | GitHub REST API only |

For the API fallback the token is taken from `UpdaterOptions.Token`, env `UPDATER_GITHUB_TOKEN` / `GITHUB_TOKEN` / `GH_TOKEN`, `%LOCALAPPDATA%\R3dUpdater\token`, or `gh auth token`. It is never needed when the SSH route works.

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

Pushing a tag `vX.Y.Z` builds the package and attaches the `.nupkg` to a GitHub release.
