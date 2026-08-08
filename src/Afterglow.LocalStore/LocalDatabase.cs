using Afterglow.Core;
using Afterglow.Core.Models;
using Microsoft.Data.Sqlite;

namespace Afterglow.LocalStore;

/// <summary>SQLite-backed state owned by the desktop application.</summary>
public sealed class LocalDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalDatabase(string? databasePath = null)
    {
        databasePath ??= AppPaths.LocalDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        EnsureSchema();
    }

    public void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS connection_config (id INTEGER PRIMARY KEY CHECK(id = 1), mode INTEGER NOT NULL, remote_api_base TEXT, auth_token TEXT, client_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ui_prefs (id INTEGER PRIMARY KEY CHECK(id = 1), accent_hex TEXT NOT NULL, glass_blur REAL NOT NULL, compact_density INTEGER NOT NULL, library_root TEXT NOT NULL, download_concurrency INTEGER NOT NULL, auto_extract INTEGER NOT NULL, library_setup_done INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS installs (game_id INTEGER PRIMARY KEY, install_path TEXT NOT NULL, exe_path TEXT, version TEXT, last_launched TEXT, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pending_play_sessions (client_session_id TEXT PRIMARY KEY, game_id INTEGER NOT NULL, started_at TEXT NOT NULL, ended_at TEXT NOT NULL, duration_secs INTEGER NOT NULL, synced INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS download_jobs (id TEXT PRIMARY KEY, game_id INTEGER NOT NULL, source_url TEXT NOT NULL, host TEXT NOT NULL, status INTEGER NOT NULL, progress REAL NOT NULL, error TEXT, output_path TEXT, created_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        TryAddColumn("ui_prefs", "library_setup_done", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn("ui_prefs", "datanodes_api_key", "TEXT");
        TryAddColumn("download_jobs", "game_title", "TEXT");
    }

    private void TryAddColumn(string table, string column, string definition)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        try { command.ExecuteNonQuery(); } catch (SqliteException) { /* already exists */ }
    }

    public async Task<AppConnectionConfig> GetConnectionConfigAsync(CancellationToken cancellationToken = default) =>
        await ReadSingleAsync("SELECT mode, remote_api_base, auth_token, client_id FROM connection_config WHERE id = 1", r => new AppConnectionConfig
        {
            Mode = (BackendMode)r.GetInt32(0), RemoteApiBase = r.IsDBNull(1) ? null : r.GetString(1),
            AuthToken = r.IsDBNull(2) ? null : r.GetString(2), ClientId = r.GetString(3)
        }, new AppConnectionConfig(), cancellationToken);

    public Task SaveConnectionConfigAsync(AppConnectionConfig value, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO connection_config(id,mode,remote_api_base,auth_token,client_id) VALUES(1,$mode,$url,$token,$client) ON CONFLICT(id) DO UPDATE SET mode=$mode,remote_api_base=$url,auth_token=$token,client_id=$client",
            [("$mode", (int)value.Mode), ("$url", value.RemoteApiBase), ("$token", value.AuthToken), ("$client", value.ClientId)], cancellationToken);

    public async Task<UiPreferences> GetUiPreferencesAsync(CancellationToken cancellationToken = default) =>
        await ReadSingleAsync("SELECT accent_hex, glass_blur, compact_density, library_root, download_concurrency, auto_extract, COALESCE(library_setup_done, 0) FROM ui_prefs WHERE id = 1", r => new UiPreferences
        {
            AccentHex = r.GetString(0), GlassBlur = r.GetDouble(1), CompactDensity = r.GetInt64(2) != 0,
            LibraryRoot = r.GetString(3), DownloadConcurrency = r.GetInt32(4), AutoExtract = r.GetInt64(5) != 0,
            LibrarySetupComplete = r.GetInt64(6) != 0
        }, new UiPreferences(), cancellationToken);

    public Task SaveUiPreferencesAsync(UiPreferences value, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO ui_prefs(id,accent_hex,glass_blur,compact_density,library_root,download_concurrency,auto_extract,library_setup_done,datanodes_api_key) VALUES(1,$accent,$blur,$compact,$root,$concurrency,$extract,$setup,NULL) ON CONFLICT(id) DO UPDATE SET accent_hex=$accent,glass_blur=$blur,compact_density=$compact,library_root=$root,download_concurrency=$concurrency,auto_extract=$extract,library_setup_done=$setup,datanodes_api_key=NULL",
            [("$accent", value.AccentHex), ("$blur", value.GlassBlur), ("$compact", value.CompactDensity ? 1 : 0), ("$root", value.LibraryRoot), ("$concurrency", value.DownloadConcurrency), ("$extract", value.AutoExtract ? 1 : 0), ("$setup", value.LibrarySetupComplete ? 1 : 0)], cancellationToken);

    public Task ResetConnectionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO connection_config(id,mode,remote_api_base,auth_token,client_id) VALUES(1,$mode,NULL,NULL,$client) ON CONFLICT(id) DO UPDATE SET mode=$mode,remote_api_base=NULL,auth_token=NULL",
            [("$mode", (int)BackendMode.Unconfigured), ("$client", Guid.NewGuid().ToString("N"))], cancellationToken);

    public Task ClearDownloadJobsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM download_jobs", [], cancellationToken);

    public Task ClearPendingPlaySessionsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM pending_play_sessions", [], cancellationToken);

    public Task UpsertInstallAsync(LocalInstall value, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO installs(game_id,install_path,exe_path,version,last_launched,updated_at) VALUES($id,$path,$exe,$version,$launched,$updated) ON CONFLICT(game_id) DO UPDATE SET install_path=$path,exe_path=$exe,version=$version,last_launched=$launched,updated_at=$updated",
            [("$id", value.GameId), ("$path", value.InstallPath), ("$exe", value.ExePath), ("$version", value.InstalledVersion), ("$launched", ToDb(value.LastLaunchedAt)), ("$updated", ToDb(value.UpdatedAt))], cancellationToken);

    public async Task<LocalInstall?> GetInstallAsync(long gameId, CancellationToken cancellationToken = default) =>
        await ReadSingleAsync("SELECT game_id,install_path,exe_path,version,last_launched,updated_at FROM installs WHERE game_id=$id", ReadInstall, null, cancellationToken, [("$id", gameId)]);

    public async Task<List<LocalInstall>> GetInstallsAsync(CancellationToken cancellationToken = default) =>
        await ReadListAsync("SELECT game_id,install_path,exe_path,version,last_launched,updated_at FROM installs", ReadInstall, cancellationToken);

    public Task DeleteInstallAsync(long gameId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM installs WHERE game_id=$id", [("$id", gameId)], cancellationToken);

    public Task AddPendingPlaySessionAsync(PendingPlaySession value, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT OR REPLACE INTO pending_play_sessions(client_session_id,game_id,started_at,ended_at,duration_secs,synced) VALUES($id,$game,$start,$end,$duration,$synced)",
            [("$id", value.ClientSessionId.ToString()), ("$game", value.GameId), ("$start", ToDb(value.StartedAt)), ("$end", ToDb(value.EndedAt)), ("$duration", value.DurationSecs), ("$synced", value.Synced ? 1 : 0)], cancellationToken);

    public Task<List<PendingPlaySession>> GetUnsyncedPlaySessionsAsync(CancellationToken cancellationToken = default) =>
        ReadListAsync("SELECT client_session_id,game_id,started_at,ended_at,duration_secs,synced FROM pending_play_sessions WHERE synced=0 ORDER BY started_at", ReadSession, cancellationToken);

    public Task MarkPlaySessionsSyncedAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
        ExecuteManyAsync("UPDATE pending_play_sessions SET synced=1 WHERE client_session_id=$id", ids.Select(x => new[] { ("$id", (object?)x.ToString()) }), cancellationToken);

    public Task UpsertDownloadJobAsync(DownloadJob value, CancellationToken cancellationToken = default) =>
        ExecuteAsync("INSERT INTO download_jobs(id,game_id,source_url,host,status,progress,error,output_path,created_at,game_title) VALUES($id,$game,$url,$host,$status,$progress,$error,$output,$created,$title) ON CONFLICT(id) DO UPDATE SET status=$status,progress=$progress,error=$error,output_path=$output,game_title=COALESCE($title,game_title)",
            [("$id", value.Id.ToString()), ("$game", value.GameId), ("$url", value.SourceUrl), ("$host", value.Host), ("$status", (int)value.Status), ("$progress", value.Progress), ("$error", value.Error), ("$output", value.OutputPath), ("$created", ToDb(value.CreatedAt)), ("$title", value.GameTitle)], cancellationToken);

    public Task<List<DownloadJob>> GetDownloadJobsAsync(CancellationToken cancellationToken = default) =>
        ReadListAsync("SELECT id,game_id,source_url,host,status,progress,error,output_path,created_at,game_title FROM download_jobs ORDER BY created_at DESC", ReadJob, cancellationToken);

    public Task DeleteDownloadJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM download_jobs WHERE id=$id", [("$id", id.ToString())], cancellationToken);

    public Task ClearFinishedDownloadJobsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM download_jobs WHERE status IN ($c,$f,$x)",
            [("$c", (int)DownloadJobStatus.Completed), ("$f", (int)DownloadJobStatus.Failed), ("$x", (int)DownloadJobStatus.Cancelled)], cancellationToken);

    private async Task ExecuteAsync(string sql, IEnumerable<(string, object?)> parameters, CancellationToken ct) =>
        await ExecuteManyAsync(sql, [parameters], ct);
    private async Task ExecuteManyAsync(string sql, IEnumerable<IEnumerable<(string, object?)>> values, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var valuesForCommand in values)
            {
                await using var command = _connection.CreateCommand(); command.CommandText = sql;
                AddParameters(command, valuesForCommand);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<T> ReadSingleAsync<T>(string sql, Func<SqliteDataReader, T> map, T fallback, CancellationToken ct, IEnumerable<(string, object?)>? parameters = null)
    {
        await _gate.WaitAsync(ct);
        try { await using var c = _connection.CreateCommand(); c.CommandText = sql; AddParameters(c, parameters); await using var r = await c.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? map(r) : fallback; }
        finally { _gate.Release(); }
    }
    private async Task<List<T>> ReadListAsync<T>(string sql, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await using var c = _connection.CreateCommand(); c.CommandText = sql; await using var r = await c.ExecuteReaderAsync(ct); var result = new List<T>(); while (await r.ReadAsync(ct)) result.Add(map(r)); return result; }
        finally { _gate.Release(); }
    }
    private static void AddParameters(SqliteCommand command, IEnumerable<(string, object?)>? values)
    {
        if (values is null) return;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
    private static LocalInstall ReadInstall(SqliteDataReader r) => new() { GameId = r.GetInt64(0), InstallPath = r.GetString(1), ExePath = NullableString(r, 2), InstalledVersion = NullableString(r, 3), LastLaunchedAt = ParseNullable(r, 4), UpdatedAt = ParseNullable(r, 5) ?? DateTimeOffset.UtcNow };
    private static PendingPlaySession ReadSession(SqliteDataReader r) => new() { ClientSessionId = Guid.Parse(r.GetString(0)), GameId = r.GetInt64(1), StartedAt = ParseNullable(r, 2)!.Value, EndedAt = ParseNullable(r, 3)!.Value, DurationSecs = r.GetInt64(4), Synced = r.GetInt64(5) != 0 };
    private static DownloadJob ReadJob(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        GameId = r.GetInt64(1),
        SourceUrl = r.GetString(2),
        Host = r.GetString(3),
        Status = (DownloadJobStatus)r.GetInt32(4),
        Progress = r.GetDouble(5),
        Error = NullableString(r, 6),
        OutputPath = NullableString(r, 7),
        CreatedAt = ParseNullable(r, 8) ?? DateTimeOffset.UtcNow,
        GameTitle = r.FieldCount > 9 ? NullableString(r, 9) : null
    };
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? ParseNullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i));
    private static string? ToDb(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
    public void Dispose() { _connection.Dispose(); _gate.Dispose(); }
}
