using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class NetworkEndpoints
{
    private const int MaxAppSegmentCount = 200;

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
            string? scope,
            string? direction,
            NetworkTrafficRepository networkTrafficRepository,
            IMonitorSettingsProvider settings,
            CancellationToken cancellationToken) =>
        {
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var limit = topN is > 0 ? topN : settings.Current.TopNDefault;
            var scopeFilter = ParseScope(scope);
            var directionFilter = ParseDirection(direction);
            var summaries = await networkTrafficRepository.QueryAppSummariesAsync(
                rangeFrom,
                rangeTo,
                scopeFilter,
                directionFilter,
                limit,
                cancellationToken);

            return Results.Ok(summaries.Select(ToSummaryDto).ToArray());
        });

        app.MapGet("/api/network/apps/{appKey}/segments", async (
            string appKey,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? scope,
            string? direction,
            NetworkTrafficRepository networkTrafficRepository,
            CancellationToken cancellationToken) =>
        {
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var segmentDuration = ResolveSegmentDuration(rangeFrom, rangeTo);
            var segmentCount = CountSegments(rangeFrom, rangeTo, segmentDuration);
            if (segmentCount > MaxAppSegmentCount)
            {
                return Results.BadRequest(new
                {
                    message = $"The requested range produces {segmentCount} segments. Reduce the range or increase the segment duration."
                });
            }

            var scopeFilter = ParseScope(scope);
            var directionFilter = ParseDirection(direction);
            var segments = await networkTrafficRepository.QueryAppSegmentsAsync(
                appKey,
                rangeFrom,
                rangeTo,
                segmentDuration,
                scopeFilter,
                directionFilter,
                cancellationToken);

            return Results.Ok(segments.Select(ToSegmentDto).ToArray());
        });

        app.MapGet("/api/network/summary", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? scope,
            string? direction,
            NetworkTrafficRepository networkTrafficRepository,
            CancellationToken cancellationToken) =>
        {
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var scopeFilter = ParseScope(scope);
            var directionFilter = ParseDirection(direction);
            var totals = await networkTrafficRepository.QueryTotalsAsync(
                rangeFrom,
                rangeTo,
                scopeFilter,
                directionFilter,
                cancellationToken);

            return Results.Ok(ToPeriodSummaryDto(totals));
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

    private static AppTrafficSegmentDto ToSegmentDto(AppTrafficSegment segment)
    {
        return new AppTrafficSegmentDto
        {
            From = segment.From,
            To = segment.To,
            TotalUploadBytes = segment.TotalUploadBytes,
            TotalDownloadBytes = segment.TotalDownloadBytes,
            WanUploadBytes = segment.WanUploadBytes,
            WanDownloadBytes = segment.WanDownloadBytes,
            LanUploadBytes = segment.LanUploadBytes,
            LanDownloadBytes = segment.LanDownloadBytes,
            LoopbackUploadBytes = segment.LoopbackUploadBytes,
            LoopbackDownloadBytes = segment.LoopbackDownloadBytes,
            OtherUploadBytes = segment.OtherUploadBytes,
            OtherDownloadBytes = segment.OtherDownloadBytes
        };
    }

    private static NetworkPeriodSummaryDto ToPeriodSummaryDto(AppTrafficPeriodSummary summary)
    {
        return new NetworkPeriodSummaryDto
        {
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

    private static TimeSpan ResolveSegmentDuration(DateTimeOffset from, DateTimeOffset to)
    {
        return to - from <= TimeSpan.FromHours(24)
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromHours(12);
    }

    private static int CountSegments(DateTimeOffset from, DateTimeOffset to, TimeSpan segmentDuration)
    {
        var segmentCount = (int)Math.Ceiling((to - from).TotalSeconds / segmentDuration.TotalSeconds);
        return segmentDuration >= TimeSpan.FromHours(12) ? segmentCount + 1 : segmentCount;
    }

    private static NetworkTrafficRepository.TrafficScopeFilter ParseScope(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "wan" => NetworkTrafficRepository.TrafficScopeFilter.Wan,
            "lan" => NetworkTrafficRepository.TrafficScopeFilter.Lan,
            "loopback" => NetworkTrafficRepository.TrafficScopeFilter.Loopback,
            _ => NetworkTrafficRepository.TrafficScopeFilter.All
        };
    }

    private static NetworkTrafficRepository.TrafficDirectionFilter ParseDirection(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "upload" => NetworkTrafficRepository.TrafficDirectionFilter.Upload,
            "download" => NetworkTrafficRepository.TrafficDirectionFilter.Download,
            _ => NetworkTrafficRepository.TrafficDirectionFilter.Total
        };
    }
}
