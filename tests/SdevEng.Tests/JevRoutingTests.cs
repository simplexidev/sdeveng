namespace SdevEng.Tests;

public class JevRoutingTests
{
    [Theory]
    [InlineData(0.7, "INCLUDE")]
    [InlineData(0.1, "EXCLUDE")]
    [InlineData(0.5, "REVIEW")]
    [InlineData(-1, "REVIEW")]
    [InlineData(1.1, "REVIEW")]
    [InlineData(double.NaN, "REVIEW")]
    public void ConservativeThresholds(double probability, string expected) => Assert.Equal(expected, JevClient.Route(probability, new()));
    [Fact] public void ThresholdsAreConfigurable() => Assert.Equal("REVIEW", JevClient.Route(.75, new() { IncludeThreshold = .9 }));
    [Fact] public void RejectsInvalidThresholdConfiguration() => Assert.Throws<ArgumentException>(() => new JevSettings { IncludeThreshold = .1, ExcludeThreshold = .2 }.Validate());
}
