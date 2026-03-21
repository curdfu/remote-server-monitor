using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monitor.Storage.Services;

public sealed class DatabaseInitializationHostedService(
    DatabaseInitializer databaseInitializer,
    ILogger<DatabaseInitializationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing SQLite database schema...");
        await databaseInitializer.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
