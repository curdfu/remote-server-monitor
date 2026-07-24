using Microsoft.Data.Sqlite;

namespace Monitor.Storage.Services;

// SQLite 只有一个写者。短暂写锁冲突通过有限退避重试消化，不能让采集后台任务直接退出整个 Host。
public static class SqliteBusyRetry
{
    private const int MaxAttempts = 5;
    private const int InitialDelayMilliseconds = 100;

    public static bool IsBusy(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode is 5 or 6;
    }

    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteAsync(
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken);
    }

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (SqliteException exception) when (IsBusy(exception) && attempt < MaxAttempts)
            {
                var exponentialDelay = InitialDelayMilliseconds * (1 << (attempt - 1));
                var jitter = Random.Shared.Next(0, InitialDelayMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
            }
        }
    }
}
