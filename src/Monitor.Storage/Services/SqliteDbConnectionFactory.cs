using Microsoft.Data.Sqlite;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Configuration;

namespace Monitor.Storage.Services;

public sealed class SqliteDbConnectionFactory : IDbConnectionFactory
{
    public SqliteDbConnectionFactory()
    {
        DatabasePath = StoragePathHelper.GetDatabasePath();
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 2
        }.ToString();
    }

    public string DatabasePath { get; }

    private string ConnectionString { get; }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(ConnectionString);
    }
}
