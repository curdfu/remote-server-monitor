using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Models;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class NetworkEndpoints
{
    private const int MaxAppSegmentCount = 200;
    private const int AppKeyLength = 64;
    private const int MaxProcessNameLength = 260;
    private const int MaxDisplayNameLength = 512;
    private const int MaxExecutablePathLength = 4096;

    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/network/ignored-apps", async (
            IgnoredNetworkAppRepository ignoredNetworkAppRepository,
            CancellationToken cancellationToken) =>
        {
            var ignoredApps = await ignoredNetworkAppRepository.GetAllAsync(cancellationToken);
            return Results.Ok(ignoredApps);
        });

        app.MapPut("/api/network/ignored-apps", async (
            IgnoredNetworkAppDto request,
            IgnoredNetworkAppRepository ignoredNetworkAppRepository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateIgnoredApp(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var normalized = NormalizeIgnoredApp(request);
            var ignoredApps = await ignoredNetworkAppRepository.UpsertAsync(normalized, cancellationToken);
            return Results.Ok(ignoredApps);
        });

        app.MapDelete("/api/network/ignored-apps/{appKey}", async (
            string appKey,
            IgnoredNetworkAppRepository ignoredNetworkAppRepository,
            CancellationToken cancellationToken) =>
        {
            var normalizedAppKey = appKey.Trim().ToUpperInvariant();
            if (!IsValidAppKey(normalizedAppKey))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(IgnoredNetworkAppDto.AppKey)] = ["AppKey must be a 64-character SHA-256 hexadecimal value."]
                });
            }

            var ignoredApps = await ignoredNetworkAppRepository.RemoveAsync(
                normalizedAppKey,
                cancellationToken);
            return Results.Ok(ignoredApps);
        });

        app.MapGet("/api/network/realtime/history", (
            INetworkAggregator networkAggregator) =>
        {
            var snapshots = networkAggregator.GetRecentRealtimeSnapshots();
            return Results.Ok(snapshots.Select(ToRealtimeDto).ToArray());
        });

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

        app.MapGet("/api/network/dashboard", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? topN,
            string? scope,
            string? direction,
            NetworkTrafficRepository networkTrafficRepository,
            IgnoredNetworkAppRepository ignoredNetworkAppRepository,
            INetworkAggregator networkAggregator,
            IMonitorSettingsProvider settings,
            CancellationToken cancellationToken) =>
        {
            var (rangeFrom, rangeTo) = NormalizeRange(from, to, TimeSpan.FromHours(1));
            if (rangeFrom >= rangeTo)
            {
                return Results.BadRequest(new { message = "'from' must be earlier than 'to'." });
            }

            var ignoredApps = await ignoredNetworkAppRepository.GetAllAsync(cancellationToken);
            var requestedLimit = Math.Clamp(topN ?? settings.Current.TopNDefault, 1, 100);
            var limit = Math.Clamp(requestedLimit + ignoredApps.Count, 1, 100);
            var scopeFilter = ParseScope(scope);
            var directionFilter = ParseDirection(direction);

            // 页面原本需要三次 HTTP 往返。组合接口保持三类统计语义不变，
            // 但由服务端统一调度并返回同一个一致时间窗口的快照。
            var appsTask = networkTrafficRepository.QueryAppSummariesAsync(
                rangeFrom,
                rangeTo,
                scopeFilter,
                directionFilter,
                limit,
                cancellationToken);
            var overviewTask = networkTrafficRepository.QueryTotalsAsync(
                rangeFrom,
                rangeTo,
                NetworkTrafficRepository.TrafficScopeFilter.All,
                directionFilter,
                cancellationToken);
            var totalsTask = networkTrafficRepository.QueryTotalsAsync(
                rangeFrom,
                rangeTo,
                scopeFilter,
                NetworkTrafficRepository.TrafficDirectionFilter.Total,
                cancellationToken);

            await Task.WhenAll(appsTask, overviewTask, totalsTask);
            var realtime = networkAggregator.GetLatestRealtimeSnapshot();

            return Results.Ok(new NetworkDashboardDto
            {
                Apps = appsTask.Result.Select(ToSummaryDto).ToArray(),
                IgnoredApps = [.. ignoredApps],
                Overview = ToPeriodSummaryDto(overviewTask.Result),
                Totals = ToPeriodSummaryDto(totalsTask.Result),
                Realtime = realtime is null ? null : ToRealtimeDto(realtime)
            });
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

            var limit = Math.Clamp(topN ?? settings.Current.TopNDefault, 1, 100);
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

    private static Dictionary<string, string[]> ValidateIgnoredApp(IgnoredNetworkAppDto request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsValidAppKey(request.AppKey?.Trim()))
        {
            errors[nameof(request.AppKey)] = ["AppKey must be a 64-character SHA-256 hexadecimal value."];
        }

        if (string.IsNullOrWhiteSpace(request.ProcessName) ||
            request.ProcessName.Trim().Length > MaxProcessNameLength)
        {
            errors[nameof(request.ProcessName)] = [$"ProcessName is required and must not exceed {MaxProcessNameLength} characters."];
        }

        if (request.DisplayName?.Trim().Length > MaxDisplayNameLength)
        {
            errors[nameof(request.DisplayName)] = [$"DisplayName must not exceed {MaxDisplayNameLength} characters."];
        }

        if (request.ExecutablePath?.Trim().Length > MaxExecutablePathLength)
        {
            errors[nameof(request.ExecutablePath)] = [$"ExecutablePath must not exceed {MaxExecutablePathLength} characters."];
        }

        return errors;
    }

    private static bool IsValidAppKey(string? appKey)
    {
        return appKey is { Length: AppKeyLength } &&
               appKey.All(character => char.IsAsciiHexDigit(character));
    }

    private static IgnoredNetworkAppDto NormalizeIgnoredApp(IgnoredNetworkAppDto request)
    {
        return new IgnoredNetworkAppDto
        {
            AppKey = request.AppKey.Trim().ToUpperInvariant(),
            ProcessName = request.ProcessName.Trim(),
            DisplayName = NormalizeOptionalText(request.DisplayName),
            ExecutablePath = NormalizeOptionalText(request.ExecutablePath)
        };
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
            ExecutablePath = summary.ExecutablePath,
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
        var rollupWindowDuration = TimeSpan.FromSeconds(NetworkTrafficRepository.RollupWindowDurationSeconds);
        if (segmentDuration == rollupWindowDuration)
        {
            return CountRollupAlignedSegments(from, to, rollupWindowDuration);
        }

        return (int)Math.Ceiling((to - from).TotalSeconds / segmentDuration.TotalSeconds);
    }

    private static int CountRollupAlignedSegments(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan rollupWindowDuration)
    {
        var rangeFrom = from.ToUniversalTime();
        var rangeTo = to.ToUniversalTime();
        var count = 0;
        var segmentFrom = rangeFrom;
        var nextAlignedWindowStart = AlignUpToWindow(segmentFrom, rollupWindowDuration);
        if (segmentFrom < nextAlignedWindowStart)
        {
            segmentFrom = nextAlignedWindowStart < rangeTo ? nextAlignedWindowStart : rangeTo;
            count++;
        }

        while (segmentFrom < rangeTo)
        {
            segmentFrom = segmentFrom.Add(rollupWindowDuration);
            count++;
        }

        return count;
    }

    private static DateTimeOffset AlignUpToWindow(DateTimeOffset value, TimeSpan windowDuration)
    {
        var utcValue = value.ToUniversalTime();
        var alignedTicks = utcValue.Ticks - utcValue.Ticks % windowDuration.Ticks;
        var alignedValue = new DateTimeOffset(alignedTicks, TimeSpan.Zero);
        return alignedValue == utcValue ? alignedValue : alignedValue.Add(windowDuration);
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
