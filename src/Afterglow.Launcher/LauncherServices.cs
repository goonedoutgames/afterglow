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
    /// <summary>Uploads the newest manual save found under Ren'Py game/saves (skips auto-*).</summary>
    public async Task<GameSave?> UploadNewestAsync(long gameId, string installPath, CancellationToken cancellationToken = default)
    {
        var file = FindNewestSave(installPath);
        if (file is null) return null;
        var extension = Path.GetExtension(file);
        var name = $"backup_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}{extension}";
        return await hubClient.UploadSaveAsync(gameId, file, name, cancellationToken);
    }

    /// <summary>
    /// Preferred folder to write a downloaded cloud save into.
    /// Prefers nested layouts like InstallRoot/Game-win/game/saves over InstallRoot/saves.
    /// </summary>
    public static string ResolveLocalSaveDirectory(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is empty.", nameof(installPath));

        var existing = EnumerateSaveDirCandidates(installPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing.Count > 0)
        {
            string? ScorePick()
            {
                // Prefer a game/saves folder that already has manual saves.
                foreach (var dir in existing.Where(IsGameSavesPath))
                {
                    if (HasManualSaves(dir)) return dir;
                }
                var anyGameSaves = existing.FirstOrDefault(IsGameSavesPath);
                if (anyGameSaves is not null) return anyGameSaves;
                foreach (var dir in existing)
                {
                    if (HasManualSaves(dir)) return dir;
                }
                return existing[0];
            }

            return ScorePick()!;
        }

        // Create under the most likely Ren'Py tree (one nested extract folder is common).
        if (Directory.Exists(installPath))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(installPath))
                {
                    var nestedGame = Path.Combine(child, "game");
                    if (!Directory.Exists(nestedGame)) continue;
                    var saves = Path.Combine(nestedGame, "saves");
                    Directory.CreateDirectory(saves);
                    return saves;
                }
            }
            catch { /* ignore */ }
        }

        var directGame = Path.Combine(installPath, "game");
        if (Directory.Exists(directGame))
        {
            var saves = Path.Combine(directGame, "saves");
            Directory.CreateDirectory(saves);
            return saves;
        }

        // Last resort: still use game/saves so we don't invent a non-Ren'Py top-level saves/.
        var created = Path.Combine(installPath, "game", "saves");
        Directory.CreateDirectory(created);
        return created;
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
                candidates.AddRange(Directory.EnumerateFiles(dir, "*", depth).Where(IsManualSave));
            }
            catch { /* ignore locked dirs */ }
        }

        foreach (var root in EnumerateSaveDirCandidates(installPath).Where(Directory.Exists))
            Collect(root, SearchOption.AllDirectories);

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

    /// <summary>
    /// Candidate Ren'Py save folders: install/game/saves, install/*/game/saves, and any deeper …/game/saves.
    /// </summary>
    public static IEnumerable<string> EnumerateSaveDirCandidates(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) yield break;

        yield return Path.Combine(installPath, "game", "saves");
        yield return Path.Combine(installPath, "game", "Save");

        if (!Directory.Exists(installPath)) yield break;

        // Common: InstallRoot/<extracted-folder>/game/saves
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(installPath); }
        catch { yield break; }

        foreach (var child in children)
        {
            yield return Path.Combine(child, "game", "saves");
            yield return Path.Combine(child, "game", "Save");
        }

        // Deeper existing …/game/saves only (do not invent paths here).
        IEnumerable<string> savesDirs;
        try { savesDirs = Directory.EnumerateDirectories(installPath, "saves", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var dir in savesDirs)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(dir));
            if (string.Equals(parent, "game", StringComparison.OrdinalIgnoreCase))
                yield return dir;
        }
    }

    private static bool IsGameSavesPath(string dir)
    {
        var norm = dir.Replace('\\', '/').TrimEnd('/');
        return norm.EndsWith("/game/saves", StringComparison.OrdinalIgnoreCase)
               || norm.EndsWith("/game/Save", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasManualSaves(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly).Any(IsManualSave);
        }
        catch { return false; }
    }

    private static string CompactName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>Ren'Py autosaves are typically auto-1-….save / auto_….save — skip those.</summary>
    public static bool IsAutoSaveName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = Path.GetFileName(fileName);
        return name.StartsWith("auto-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("auto_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManualSave(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return false;
        if (IsAutoSaveName(name)) return false;

        var lower = name.ToLowerInvariant();
        if (lower is "script_version.txt" or "project.json" or "android.json") return false;
        if (lower.EndsWith(".rpy") || lower.EndsWith(".rpyc") || lower.EndsWith(".py")) return false;

        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".save" or ".sav") return true;
        if (ext is ".json" or ".dat" or ".bin")
        {
            return lower.Contains("save", StringComparison.Ordinal)
                   || lower.Contains("persistent", StringComparison.Ordinal)
                   || char.IsDigit(lower[0]);
        }
        return lower.Contains("save", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("persistent", StringComparison.OrdinalIgnoreCase);
    }
}
