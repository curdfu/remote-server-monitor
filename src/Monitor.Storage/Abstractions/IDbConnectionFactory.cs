using Microsoft.Data.Sqlite;

namespace Monitor.Storage.Abstractions;

public interface IDbConnectionFactory
{
    string DatabasePath { get; }
    SqliteConnection CreateConnection();
}
