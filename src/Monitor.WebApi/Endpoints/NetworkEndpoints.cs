using Microsoft.Extensions.Options;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class NetworkEndpoints
{
    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/network/realtime", (
            INetworkAggregator networkAggregator) =>
        {
            var snapshot = networkAggregator.GetLatestRealtimeSnapshot();
            if (snapshot is null)
            {
                return Results.Problem(
                    detail: "Network realtime cache is not ready yet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(ToRealtimeDto(snapshot));
        });

        app.MapGet("/api/network/apps", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? topN,
            NetworkTrafficRepository networkTrafficRepository,
            IOptionsMonitor<MonitorSettings> settings,
            CancellationToken cancellationToken) =>
        {
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var limit = topN is > 0 ? topN : settings.CurrentValue.TopNDefault;
            var summaries = await networkTrafficRepository.QueryAppSummariesAsync(rangeFrom, rangeTo, limit, cancellationToken);

            return Results.Ok(summaries.Select(ToSummaryDto).ToArray());
        });

        return app;
    }

    private static NetworkRealtimeDto ToRealtimeDto(NetworkRealtimeSnapshot snapshot)
    {
        return new NetworkRealtimeDto
        {
            SampleTime = snapshot.SampleTime,
            TotalUploadBytesPerSecond = snapshot.TotalUploadBytesPerSecond,
            TotalDownloadBytesPerSecond = snapshot.TotalDownloadBytesPerSecond,
            WanUploadBytesPerSecond = snapshot.WanUploadBytesPerSecond,
            WanDownloadBytesPerSecond = snapshot.WanDownloadBytesPerSecond,
            LanUploadBytesPerSecond = snapshot.LanUploadBytesPerSecond,
            LanDownloadBytesPerSecond = snapshot.LanDownloadBytesPerSecond
        };
    }

    private static AppTrafficSummaryDto ToSummaryDto(AppTrafficPeriodSummary summary)
    {
        return new AppTrafficSummaryDto
        {
            AppKey = summary.AppKey,
            ProcessName = summary.ProcessName,
            DisplayName = summary.DisplayName,
            TotalUploadBytes = summary.TotalUploadBytes,
            TotalDownloadBytes = summary.TotalDownloadBytes,
            WanUploadBytes = summary.WanUploadBytes,
            WanDownloadBytes = summary.WanDownloadBytes,
            LanUploadBytes = summary.LanUploadBytes,
            LanDownloadBytes = summary.LanDownloadBytes,
            LoopbackUploadBytes = summary.LoopbackUploadBytes,
            LoopbackDownloadBytes = summary.LoopbackDownloadBytes,
            OtherUploadBytes = summary.OtherUploadBytes,
            OtherDownloadBytes = summary.OtherDownloadBytes
        };
    }

    private static (DateTimeOffset From, DateTimeOffset To) NormalizeRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        TimeSpan defaultWindow)
    {
        var rangeTo = to ?? DateTimeOffset.UtcNow;
        var rangeFrom = from ?? rangeTo.Subtract(defaultWindow);
        return (rangeFrom, rangeTo);
    }
}
