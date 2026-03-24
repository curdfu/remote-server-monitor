using Microsoft.Data.Sqlite;

namespace Monitor.Storage.Configuration;

public static class PersistedSettingsLoader
{
    public static IDictionary<string, string?> Load(string? baseDirectory = null)
    {
        try
        {
            var databasePath = GetDatabasePath(baseDirectory);
            if (!File.Exists(databasePath))
            {
                return new Dictionary<string, string?>();
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            connection.Open();

            if (!TableExists(connection, "settings"))
            {
                return new Dictionary<string, string?>();
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT http_port,
                                         hardware_sample_interval_ms,
                                         network_sample_interval_ms,
                                         aggregate_interval_seconds,
                                         history_retention_days,
                                         top_n_default
                                  FROM settings
                                  WHERE id = 1
                                  LIMIT 1;
                                  """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new Dictionary<string, string?>();
            }

            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:HttpPort"] = reader.GetInt32(0).ToString(),
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:HardwareSampleIntervalMs"] = reader.GetInt32(1).ToString(),
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:NetworkSampleIntervalMs"] = reader.GetInt32(2).ToString(),
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:AggregateIntervalSeconds"] = reader.GetInt32(3).ToString(),
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:HistoryRetentionDays"] = reader.GetInt32(4).ToString(),
                [$"{Monitor.Contracts.Options.MonitorSettings.SectionName}:TopNDefault"] = reader.GetInt32(5).ToString()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.UtcNow:O}] Failed to load persisted monitor settings from SQLite. Falling back to appsettings defaults. {exception}");
            return new Dictionary<string, string?>();
        }
    }

    public static string GetDatabasePath(string? baseDirectory = null)
    {
        return StoragePathHelper.GetDatabasePath(baseDirectory);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT EXISTS(
                                  SELECT 1
                                  FROM sqlite_master
                                  WHERE type = 'table'
                                    AND name = $tableName
                              );
                              """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 1;
    }
}
