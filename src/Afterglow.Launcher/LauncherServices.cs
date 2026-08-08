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

    /// <summary>Preferred folder to write a downloaded cloud save into for this install.</summary>
    public static string ResolveLocalSaveDirectory(string installPath)
    {
        foreach (var dir in KnownSaveRoots(installPath))
        {
            if (Directory.Exists(dir)) return dir;
        }

        var gameDir = Path.Combine(installPath, "game");
        if (Directory.Exists(gameDir))
        {
            var nested = Path.Combine(gameDir, "saves");
            Directory.CreateDirectory(nested);
            return nested;
        }

        var fallback = Path.Combine(installPath, "saves");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static string? FindNewestSave(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return null;

        var candidates = new List<string>();
        void Collect(string dir, SearchOption depth)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(dir, "*", depth).Where(IsLikelySave));
            }
            catch { /* ignore locked dirs */ }
        }

        foreach (var root in KnownSaveRoots(installPath))
            Collect(root, SearchOption.AllDirectories);

        // Also scan install root shallowly for loose save files.
        Collect(installPath, SearchOption.TopDirectoryOnly);

        try
        {
            var renpyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenPy");
            if (Directory.Exists(renpyRoot))
            {
                var gameName = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var compact = CompactName(gameName);
                foreach (var dir in Directory.EnumerateDirectories(renpyRoot))
                {
                    var leaf = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(leaf)) continue;
                    var match = (!string.IsNullOrWhiteSpace(gameName)
                                 && leaf.Contains(gameName, StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(compact)
                                    && CompactName(leaf).Contains(compact, StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(compact)
                                    && compact.Contains(CompactName(leaf), StringComparison.OrdinalIgnoreCase)
                                    && CompactName(leaf).Length >= 4);
                    if (match)
                        Collect(dir, SearchOption.AllDirectories);
                }
            }
        }
        catch { /* ignore */ }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<string> KnownSaveRoots(string installPath)
    {
        yield return Path.Combine(installPath, "game", "saves");
        yield return Path.Combine(installPath, "game", "Save");
        yield return Path.Combine(installPath, "saves");
        yield return Path.Combine(installPath, "Save");
        yield return Path.Combine(installPath, "save");
    }

    private static string CompactName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static bool IsLikelySave(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return false;
        // Skip obvious non-saves under game/
        var lower = name.ToLowerInvariant();
        if (lower is "script_version.txt" or "project.json" or "android.json") return false;
        if (lower.EndsWith(".rpy") || lower.EndsWith(".rpyc") || lower.EndsWith(".py")) return false;

        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".save" or ".sav") return true;
        if (ext is ".json" or ".dat" or ".bin")
        {
            // Prefer names that look like slots / persistent
            return lower.Contains("save", StringComparison.Ordinal)
                   || lower.Contains("persistent", StringComparison.Ordinal)
                   || lower.StartsWith("auto-", StringComparison.Ordinal)
                   || char.IsDigit(lower[0]);
        }
        return lower.Contains("save", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("persistent", StringComparison.OrdinalIgnoreCase);
    }
}
