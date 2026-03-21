using Microsoft.Data.Sqlite;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

public sealed class SqliteDbConnectionFactory : IDbConnectionFactory
{
    public SqliteDbConnectionFactory()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        DatabasePath = Path.Combine(dataDirectory, "monitor.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public string DatabasePath { get; }

    private string ConnectionString { get; }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(ConnectionString);
    }
}
