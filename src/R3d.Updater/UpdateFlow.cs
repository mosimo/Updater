namespace R3d.Updater;

/// <summary>
/// The user-facing parts of updating (prompts and wording) with the UI left to the app: pass your own
/// confirm/message dialogs. Both methods must be called on the UI thread.
/// </summary>
public static class UpdateFlow
{
    /// <summary>
    /// Asks "restart now?" whenever the background loop has an update ready. Answering no leaves it to
    /// install on the next launch.
    /// </summary>
    public static void PromptWhenReady(UpdateManager manager, Func<string, string, Task<bool>> confirm)
    {
        var asked = false;
        manager.UpdateReady += async (_, staged) =>
        {
            if (asked || !manager.CanSelfInstall) return;
            asked = true;
            try
            {
                if (await confirm("Update ready", $"Version {staged.Version} has been downloaded (you have {manager.CurrentVersion}).\n\nRestart now to install it? Otherwise it installs the next time the app starts."))
                    manager.ApplyAndRestart();
            }
            catch (Exception ex)
            {
                // async void: never let a failed install take the app down; the next check retries.
                asked = false;
                manager.ReportError(ex);
            }
        };
    }

    /// <summary>The "Check for updates" button: check, ask, download, install, restart. Reports every outcome through <paramref name="message"/>.</summary>
    /// <param name="status">Optional short progress text for the button, e.g. "Downloading 42%". Null text means finished.</param>
    public static async Task CheckInteractiveAsync(
        UpdateManager manager,
        Func<string, string, Task<bool>> confirm,
        Func<string, string, Task> message,
        Action<string?>? status = null)
    {
        const string title = "Check for updates";
        void Progress(object? _, DownloadProgressEventArgs e) => status?.Invoke($"Downloading {e.Fraction:P0}");
        try
        {
            if (manager.Staged is { } ready && ready.Version > manager.CurrentVersion)
            {
                if (await confirm(title, $"Version {ready.Version} is downloaded and ready. Restart now to install it?"))
                    manager.ApplyAndRestart();
                return;
            }

            status?.Invoke("Checking…");
            var update = await manager.CheckAsync();
            if (update is null)
            {
                await message(title, $"You're up to date (version {manager.CurrentVersion}).");
                return;
            }

            var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n\n" + Trim(update.Notes!, 600);
            if (!await confirm(title, $"Version {update.Version} is available (you have {manager.CurrentVersion}).{notes}\n\nDownload and install it now? The app will restart."))
                return;

            manager.DownloadProgress += Progress;
            status?.Invoke("Downloading…");
            await manager.DownloadAsync(update);
            manager.ApplyAndRestart();
        }
        catch (Exception ex) when (ex is UpdateException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await message(title, "The update failed: " + ex.Message);
        }
        finally
        {
            manager.DownloadProgress -= Progress;
            status?.Invoke(null);
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s.Trim() : s[..max].TrimEnd() + "…";
}
