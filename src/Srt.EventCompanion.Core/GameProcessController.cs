using System.Diagnostics;

namespace Srt.EventCompanion.Core;

/// <summary>Finds and closes Assetto Corsa and Content Manager on the rig. No server-side action is taken.</summary>
public sealed class GameProcessController
{
    // Assetto Corsa first, so the driver leaves the server before the launcher goes away.
    private static readonly string[] GameProcessNames = { "acs", "acs_x86", "AssettoCorsa" };
    private static readonly string[] LauncherProcessNames = { "Content Manager" };

    public bool IsAnyRunning() => GameProcessNames.Concat(LauncherProcessNames).Any(name => Process.GetProcessesByName(name).Length > 0);

    /// <summary>
    /// Asks each window to close, waits <paramref name="grace"/>, then ends whatever is still running.
    /// Returns the names of any process that could not be closed.
    /// </summary>
    public async Task<IReadOnlyList<string>> CloseAllAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        var stillRunning = new List<string>();
        foreach (var name in GameProcessNames.Concat(LauncherProcessNames))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (!await CloseAsync(process, grace, cancellationToken)) stillRunning.Add(name);
                }
            }
        }
        return stillRunning.Distinct().ToList();
    }

    private static async Task<bool> CloseAsync(Process process, TimeSpan grace, CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited) return true;
            if (process.CloseMainWindow() && await WaitForExitAsync(process, grace, cancellationToken)) return true;
            process.Kill(entireProcessTree: true);
            return await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return true; // Exited between the lookup and the close.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return process.HasExited;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }
}
