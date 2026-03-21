using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Monitor.Contracts.Options;

public sealed class AddressClassificationSettings : IValidatableObject
{
    public bool TreatPrivateAddressesAsLan { get; set; } = true;

    public bool TreatLocalSubnetsAsLan { get; set; } = true;

    public bool TreatLoopbackAsLoopback { get; set; } = true;

    public string[] AdditionalLanCidrs { get; set; } = [];

    public string[] AdditionalWanCidrs { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateCidrs(AdditionalLanCidrs, nameof(AdditionalLanCidrs)))
        {
            yield return result;
        }

        foreach (var result in ValidateCidrs(AdditionalWanCidrs, nameof(AdditionalWanCidrs)))
        {
            yield return result;
        }
    }

    private static IEnumerable<ValidationResult> ValidateCidrs(IEnumerable<string>? cidrs, string propertyName)
    {
        if (cidrs is null)
        {
            yield break;
        }

        var index = 0;
        foreach (var cidr in cidrs)
        {
            if (!IsValidCidr(cidr))
            {
                yield return new ValidationResult(
                    $"Invalid CIDR value '{cidr}' in {propertyName}[{index}].",
                    [$"{propertyName}[{index}]"]);
            }

            index++;
        }
    }

    private static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => prefixLength is >= 0 and <= 32,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => prefixLength is >= 0 and <= 128,
            _ => false
        };
    }
}
