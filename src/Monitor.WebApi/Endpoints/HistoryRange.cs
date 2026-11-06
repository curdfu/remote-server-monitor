namespace Monitor.WebApi.Endpoints;

internal static class HistoryRange
{
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(31);
    private static readonly DateTimeOffset Earliest = DateTimeOffset.MinValue.AddDays(1);
    private static readonly DateTimeOffset Latest = DateTimeOffset.MaxValue.AddDays(-1);

    public static bool TryNormalize(
        DateTimeOffset? from,
        DateTimeOffset? to,
        TimeSpan defaultWindow,
        out DateTimeOffset rangeFrom,
        out DateTimeOffset rangeTo,
        out string error)
    {
        rangeTo = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        rangeFrom = from?.ToUniversalTime() ?? rangeTo;
        error = "History timestamps are outside the supported range.";
        if (rangeTo < Earliest || rangeTo > Latest ||
            (from.HasValue && (rangeFrom < Earliest || rangeFrom > Latest)))
        {
            return false;
        }

        if (!from.HasValue)
        {
            rangeFrom = rangeTo.Subtract(defaultWindow);
        }

        if (rangeFrom >= rangeTo)
        {
            error = "'from' must be earlier than 'to'.";
            return false;
        }

        if (rangeTo - rangeFrom > MaximumDuration)
        {
            error = "The requested history range must not exceed 31 days.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
