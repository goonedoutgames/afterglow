using System.Diagnostics;
using Afterglow.Core.Models;
using Afterglow.HubClient;
using Afterglow.LocalStore;

namespace Afterglow.Launcher;

public sealed class GameLauncher(LocalDatabase database)
{
    public async Task<int> LaunchAsync(LocalInstall install, CancellationToken cancellationToken = default)
    {
        var executable = install.ExePath ?? throw new InvalidOperationException("No executable is configured for this game.");
        if (!File.Exists(executable)) throw new FileNotFoundException("Game executable was not found.", executable);
        var startedAt = DateTimeOffset.UtcNow;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? install.InstallPath,
            UseShellExecute = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the game process.");
        await process.WaitForExitAsync(cancellationToken);
        var endedAt = DateTimeOffset.UtcNow;
        await database.AddPendingPlaySessionAsync(new PendingPlaySession
        {
            GameId = install.GameId, StartedAt = startedAt, EndedAt = endedAt,
            DurationSecs = Math.Max(0, (long)(endedAt - startedAt).TotalSeconds)
        }, CancellationToken.None);
        install.LastLaunchedAt = startedAt;
        install.UpdatedAt = DateTimeOffset.UtcNow;
        await database.UpsertInstallAsync(install, CancellationToken.None);
        return process.ExitCode;
    }
}

public sealed class PlaytimeSyncService(LocalDatabase database, HubApiClient hubClient, string? clientId = null)
{
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        var pending = await database.GetUnsyncedPlaySessionsAsync(cancellationToken);
        var synced = 0;
        foreach (var group in pending.GroupBy(x => x.GameId))
        {
            var sessions = group.Select(x => new PlaySessionDto
            {
                ClientSessionId = x.ClientSessionId,
                StartedAt = x.StartedAt.UtcDateTime.ToString("O"),
                EndedAt = x.EndedAt.UtcDateTime.ToString("O"),
                DurationSecs = x.DurationSecs,
                ClientId = clientId
            }).ToList();
            await hubClient.PostPlaytimeAsync(group.Key, sessions, cancellationToken);
            await database.MarkPlaySessionsSyncedAsync(group.Select(x => x.ClientSessionId), cancellationToken);
            synced += sessions.Count;
        }
        return synced;
    }
}

public sealed class RenpySaveSync(HubApiClient hubClient)
{
    /// <summary>Uploads the newest save found under common Ren'Py / game save locations.</summary>
    public async Task<GameSave?> UploadNewestAsync(long gameId, string installPath, CancellationToken cancellationToken = default)
    {
        var file = FindNewestSave(installPath);
        if (file is null) return null;
        var extension = Path.GetExtension(file);
        var name = $"auto_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}{extension}";
        return await hubClient.UploadSaveAsync(gameId, file, name, cancellationToken);
    }

    public static string? FindNewestSave(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        var candidates = new List<string>();
        void Collect(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsLikelySave));
            }
            catch { /* ignore locked dirs */ }
        }

        Collect(Path.Combine(installPath, "game", "saves"));
        Collect(Path.Combine(installPath, "saves"));
        Collect(Path.Combine(installPath, "Save"));
        Collect(Path.Combine(installPath, "save"));

        try
        {
            var renpyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenPy");
            if (Directory.Exists(renpyRoot))
            {
                var gameName = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var dir in Directory.EnumerateDirectories(renpyRoot))
                {
                    var leaf = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(gameName)
                        && leaf.Contains(gameName, StringComparison.OrdinalIgnoreCase))
                        Collect(dir);
                }
            }
        }
        catch { /* ignore */ }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsLikelySave(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".save" or ".sav" or ".json" or ".dat" or ".bin"
               || name.Contains("save", StringComparison.OrdinalIgnoreCase);
    }
}
