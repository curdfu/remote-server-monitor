using System.ComponentModel.DataAnnotations;

namespace Monitor.Contracts.Options;

public sealed class MonitorSettings : IValidatableObject
{
    public const string SectionName = "Monitor";

    [Range(1, 65535)]
    public int HttpPort { get; set; } = 5188;

    [Range(500, 60000)]
    public int HardwareSampleIntervalMs { get; set; } = 1000;

    [Range(500, 60000)]
    public int NetworkRealtimeIntervalMs { get; set; } = 1000;

    [Range(1, 3600)]
    public int AggregateIntervalSeconds { get; set; } = 10;

    [Range(1, 3650)]
    public int HistoryRetentionDays { get; set; } = 30;

    [Range(1, 100)]
    public int TopNDefault { get; set; } = 10;

    [Range(1, 128)]
    public int EtwBufferSizeMb { get; set; } = 4;

    public bool EnableEtwTargetEventLogging { get; set; }

    public string EtwTargetEventLoggingProtocolFilter { get; set; } = "all";

    [Required]
    public AddressClassificationSettings AddressClassification { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AddressClassification is null)
        {
            yield return new ValidationResult(
                $"{nameof(AddressClassification)} is required.",
                [nameof(AddressClassification)]);
            yield break;
        }

        var results = new List<ValidationResult>();
        var nestedContext = new ValidationContext(AddressClassification);
        Validator.TryValidateObject(AddressClassification, nestedContext, results, validateAllProperties: true);

        foreach (var result in results)
        {
            var memberNames = result.MemberNames.Select(static name => $"{nameof(AddressClassification)}.{name}");
            yield return new ValidationResult(result.ErrorMessage, memberNames);
        }

        if (!IsValidEtwTargetEventLoggingProtocolFilter(EtwTargetEventLoggingProtocolFilter))
        {
            yield return new ValidationResult(
                $"{nameof(EtwTargetEventLoggingProtocolFilter)} must be one of: all, tcp, udp.",
                [nameof(EtwTargetEventLoggingProtocolFilter)]);
        }
    }

    private static bool IsValidEtwTargetEventLoggingProtocolFilter(string? value)
    {
        return value?.Trim().ToLowerInvariant() is "all" or "tcp" or "udp";
    }
}
