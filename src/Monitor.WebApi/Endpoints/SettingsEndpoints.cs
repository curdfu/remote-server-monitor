using System.ComponentModel.DataAnnotations;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Storage.Repositories;

namespace Monitor.WebApi.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", async (
            SettingsRepository settingsRepository,
            IMonitorSettingsProvider settings,
            CancellationToken cancellationToken) =>
        {
            var persisted = await settingsRepository.GetAsync(cancellationToken);
            return Results.Ok(persisted ?? ToDto(settings.Current));
        });

        app.MapPost("/api/settings", async (
            AppSettingsDto request,
            SettingsRepository settingsRepository,
            IMonitorSettingsProvider currentSettings,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var runtimeSettings = currentSettings.Current;
            var validationErrors = Validate(request, runtimeSettings);
            if (validationErrors.Count > 0)
            {
                return Results.ValidationProblem(validationErrors);
            }

            await settingsRepository.SaveAsync(request, cancellationToken);
            var appliedSettings = currentSettings.Update(request);

            if (request.HttpPort != appliedSettings.HttpPort)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpoints)).LogWarning(
                    "HTTP port change was persisted but cannot be applied dynamically. CurrentPort={CurrentPort}, RequestedPort={RequestedPort}. A service restart is still required for the new port.",
                    appliedSettings.HttpPort,
                    request.HttpPort);
            }

            return Results.Ok(request);
        });

        return app;
    }

    private static Dictionary<string, string[]> Validate(AppSettingsDto request, MonitorSettings currentSettings)
    {
        var candidate = new MonitorSettings
        {
            HttpPort = request.HttpPort,
            HardwareSampleIntervalMs = request.HardwareSampleIntervalMs,
            AggregateIntervalSeconds = request.AggregateIntervalSeconds,
            HistoryRetentionDays = request.HistoryRetentionDays,
            TopNDefault = request.TopNDefault,
            EtwBufferSizeMb = currentSettings.EtwBufferSizeMb,
            EnableEtwTargetEventLogging = currentSettings.EnableEtwTargetEventLogging,
            EtwTargetEventLoggingProtocolFilter = currentSettings.EtwTargetEventLoggingProtocolFilter,
            AddressClassification = currentSettings.AddressClassification
        };

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(candidate, new ValidationContext(candidate), results, validateAllProperties: true);

        return results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
                .Select(memberName => new
                {
                    Key = string.IsNullOrWhiteSpace(memberName) ? "settings" : memberName,
                    Message = result.ErrorMessage ?? "Invalid setting value."
                }))
            .GroupBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static x => x.Message).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static AppSettingsDto ToDto(MonitorSettings settings)
    {
        return new AppSettingsDto
        {
            HttpPort = settings.HttpPort,
            HardwareSampleIntervalMs = settings.HardwareSampleIntervalMs,
            AggregateIntervalSeconds = settings.AggregateIntervalSeconds,
            HistoryRetentionDays = settings.HistoryRetentionDays,
            TopNDefault = settings.TopNDefault
        };
    }
}
