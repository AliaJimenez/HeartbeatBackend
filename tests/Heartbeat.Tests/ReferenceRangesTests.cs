using Heartbeat.Shared;
using System.Text.Json;

namespace Heartbeat.Tests;

public class ReferenceRangesTests
{
    [Fact]
    public void EmbeddedResource_Loads()
    {
        Assert.False(string.IsNullOrWhiteSpace(ReferenceRanges.Version));
        Assert.Contains("indicators", ReferenceRanges.Json);
    }

    [Fact]
    public void EmbeddedResource_Parses()
    {
        using var doc = ReferenceRanges.Parse();
        Assert.True(doc.RootElement.TryGetProperty("indicators", out var indicators));
        Assert.Equal(JsonValueKind.Array, indicators.ValueKind);
        Assert.True(indicators.GetArrayLength() > 0);
    }
}