using Monitor.WebApi.Endpoints;
using Monitor.WebApi.Hubs;
using Monitor.WebApi.Middleware;

namespace Monitor.WebApi.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication MapMonitorWebApi(this WebApplication app)
    {
        var indexFilePath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
        if (File.Exists(indexFilePath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.UseCors();

        app.UseMiddleware<GlobalExceptionMiddleware>();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            utcNow = DateTimeOffset.UtcNow
        }));

        app.MapOverviewEndpoints();
        app.MapHardwareEndpoints();
        app.MapNetworkEndpoints();
        app.MapSettingsEndpoints();
        app.MapHub<MonitorHub>("/hubs/monitor");

        if (File.Exists(indexFilePath))
        {
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}
