using Afterglow.Core;
using Xunit;

namespace Afterglow.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("v0.1.14", "0.1.14")]
    [InlineData("0.1.14+abc", "0.1.14")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    public void Normalize_strips_prefix_and_build(string raw, string expected) =>
        Assert.Equal(expected, SemVer.Normalize(raw));

    [Theory]
    [InlineData("0.1.13", "0.1.14", true)]
    [InlineData("0.1.14", "0.1.14", false)]
    [InlineData("0.2.0", "0.1.99", false)]
    [InlineData("0.1.14", "0.2.0-beta.1", false)]
    [InlineData("v0.1.10", "v0.1.11", true)]
    [InlineData("0.0.0-ci.12", "0.1.0", true)]
    public void IsNewerStable_compares_core_versions(string current, string candidate, bool newer) =>
        Assert.Equal(newer, SemVer.IsNewerStable(current, candidate));

    [Theory]
    [InlineData("0.0.0-dev")]
    [InlineData("0.0.0-ci.42")]
    [InlineData("1.2.3-beta.1")]
    public void TryParse_marks_ci_and_dev_as_prerelease(string raw)
    {
        Assert.True(SemVer.TryParse(raw, out _, out var pre));
        Assert.True(pre);
    }

    [Fact]
    public void TryParse_marks_prerelease()
    {
        Assert.True(SemVer.TryParse("0.0.0-dev", out _, out var pre));
        Assert.True(pre);
        Assert.True(SemVer.TryParse("1.2.3", out var v, out var stablePre));
        Assert.False(stablePre);
        Assert.Equal((1, 2, 3), v);
    }
}
