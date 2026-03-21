using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
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
            IOptionsMonitor<MonitorSettings> settings,
            CancellationToken cancellationToken) =>
        {
            var persisted = await settingsRepository.GetAsync(cancellationToken);
            return Results.Ok(persisted ?? ToDto(settings.CurrentValue));
        });

        app.MapPost("/api/settings", async (
            AppSettingsDto request,
            SettingsRepository settingsRepository,
            IOptionsMonitor<MonitorSettings> currentSettings,
            CancellationToken cancellationToken) =>
        {
            var validationErrors = Validate(request, currentSettings.CurrentValue);
            if (validationErrors.Count > 0)
            {
                return Results.ValidationProblem(validationErrors);
            }

            await settingsRepository.SaveAsync(request, cancellationToken);
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
            NetworkSampleIntervalMs = request.NetworkSampleIntervalMs,
            AggregateIntervalSeconds = request.AggregateIntervalSeconds,
            HistoryRetentionDays = request.HistoryRetentionDays,
            TopNDefault = request.TopNDefault,
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
            NetworkSampleIntervalMs = settings.NetworkSampleIntervalMs,
            AggregateIntervalSeconds = settings.AggregateIntervalSeconds,
            HistoryRetentionDays = settings.HistoryRetentionDays,
            TopNDefault = settings.TopNDefault
        };
    }
}
