using Monitor.WebApi.Endpoints;
using Monitor.WebApi.Hubs;
using Monitor.WebApi.Middleware;

namespace Monitor.WebApi.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication MapMonitorWebApi(this WebApplication app)
    {
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

        return app;
    }
}
