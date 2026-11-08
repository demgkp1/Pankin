using Pankin.Core.Updates;
using Xunit;

namespace Pankin.Core.Tests.Updates;

public class VersionCompareTests
{
    [Theory]
    [InlineData("0.1.9", "0.1.10", -1)]
    [InlineData("0.1.10", "0.1.9", 1)]
    [InlineData("1.2.0", "1.2", 0)]
    [InlineData("1.2.0.0", "1.2.0+abc", 0)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("v0.1.36", "0.1.36", 0)]
    [InlineData("v1.0.0", "v1.0.1", -1)]
    [InlineData("0.1.36", "0.1.036", 0)]
    public void CompareVersions_ShouldMatchExpectedOrder(string v1, string v2, int expectedSign)
    {
        int result = VersionCompare.CompareVersions(v1, v2);
        int sign = Math.Sign(result);
        Assert.Equal(expectedSign, sign);
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("2.4.1.8", true)]
    [InlineData("invalid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_ShouldValidateCorrectly(string? version, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsValid(version));
    }
}
