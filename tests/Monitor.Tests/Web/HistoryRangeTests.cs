using Monitor.WebApi.Endpoints;

namespace Monitor.Tests.Web;

public sealed class HistoryRangeTests
{
    [Fact]
    public void NormalizesOffsetsToTheSameUtcHalfOpenRange()
    {
        var from = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(8));
        Assert.True(HistoryRange.TryNormalize(from, from.AddHours(1), TimeSpan.FromHours(1), out var start, out var end, out var error));
        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal(TimeSpan.Zero, end.Offset);
        Assert.Equal(0, start.Hour);
        Assert.Equal(TimeSpan.FromHours(1), end - start);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public void BoundsTheSingleRequestWithoutChangingRetention(int days, bool accepted)
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(accepted, HistoryRange.TryNormalize(from, from.AddDays(days), TimeSpan.FromHours(1), out _, out _, out _));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("9999-12-31T23:59:59+00:00")]
    public void RejectsExtremeDatesBeforeRepositoryWindowArithmetic(string timestamp)
    {
        Assert.False(HistoryRange.TryNormalize(null, DateTimeOffset.Parse(timestamp), TimeSpan.FromHours(1), out _, out _, out var error));
        Assert.Contains("supported range", error);
    }

    [Fact]
    public void OmittedStartUsesTheEndpointDefaultWindow()
    {
        var to = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(8));
        Assert.True(HistoryRange.TryNormalize(null, to, TimeSpan.FromHours(24), out var from, out var end, out _));
        Assert.Equal(TimeSpan.FromHours(24), end - from);
        Assert.Equal(TimeSpan.Zero, from.Offset);
    }
}
