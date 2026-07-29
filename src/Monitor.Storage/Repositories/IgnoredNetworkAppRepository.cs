using Microsoft.Extensions.Logging;
using Monitor.Contracts.Dtos;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Services;

namespace Monitor.Storage.Repositories;

// 全局忽略列表只在首次访问时读取 SQLite，正常 dashboard 刷新直接使用内存快照。
// 只有用户忽略或恢复应用时才写库，避免给机械硬盘增加周期性 I/O。
public sealed class IgnoredNetworkAppRepository(
    IDbConnectionFactory dbConnectionFactory,
    ILogger<IgnoredNetworkAppRepository> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IgnoredNetworkAppDto[]? _snapshot;

    public async Task<IReadOnlyList<IgnoredNetworkAppDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is null)
            {
                snapshot = await LoadCoreAsync(cancellationToken);
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IgnoredNetworkAppDto>> UpsertAsync(
        IgnoredNetworkAppDto app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _snapshot ?? await LoadCoreAsync(cancellationToken);
            await SqliteBusyRetry.ExecuteAsync(
                token => UpsertCoreAsync(app, token),
                cancellationToken);

            var updated = snapshot
                .Where(item => !string.Equals(item.AppKey, app.AppKey, StringComparison.OrdinalIgnoreCase))
                .Append(app)
                .OrderBy(item => item.DisplayName ?? item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Volatile.Write(ref _snapshot, updated);

            logger.LogInformation(
                "Network ranking app ignored globally. AppKey={AppKey}, ProcessName={ProcessName}",
                app.AppKey,
                app.ProcessName);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IgnoredNetworkAppDto>> RemoveAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _snapshot ?? await LoadCoreAsync(cancellationToken);
            await SqliteBusyRetry.ExecuteAsync(
                token => RemoveCoreAsync(appKey, token),
                cancellationToken);

            var updated = snapshot
                .Where(item => !string.Equals(item.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Volatile.Write(ref _snapshot, updated);

            logger.LogInformation("Network ranking app restored globally. AppKey={AppKey}", appKey);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IgnoredNetworkAppDto[]> LoadCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT app_key,
                                     process_name,
                                     display_name,
                                     executable_path
                              FROM ignored_network_apps
                              ORDER BY COALESCE(display_name, process_name) COLLATE NOCASE;
                              """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var apps = new List<IgnoredNetworkAppDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            apps.Add(new IgnoredNetworkAppDto
            {
                AppKey = reader.GetString(0),
                ProcessName = reader.GetString(1),
                DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ExecutablePath = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }

        return apps.ToArray();
    }

    private async Task UpsertCoreAsync(
        IgnoredNetworkAppDto app,
        CancellationToken cancellationToken)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO ignored_network_apps (
                                  app_key,
                                  process_name,
                                  display_name,
                                  executable_path,
                                  created_at,
                                  updated_at
                              )
                              VALUES (
                                  $appKey,
                                  $processName,
                                  $displayName,
                                  $executablePath,
                                  $createdAt,
                                  $updatedAt
                              )
                              ON CONFLICT(app_key) DO UPDATE SET
                                  process_name = excluded.process_name,
                                  display_name = excluded.display_name,
                                  executable_path = excluded.executable_path,
                                  updated_at = excluded.updated_at;
                              """;

        var now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$appKey", app.AppKey);
        command.Parameters.AddWithValue("$processName", app.ProcessName);
        command.Parameters.AddWithValue("$displayName", (object?)app.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$executablePath", (object?)app.ExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RemoveCoreAsync(string appKey, CancellationToken cancellationToken)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM ignored_network_apps
                              WHERE app_key = $appKey;
                              """;
        command.Parameters.AddWithValue("$appKey", appKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
