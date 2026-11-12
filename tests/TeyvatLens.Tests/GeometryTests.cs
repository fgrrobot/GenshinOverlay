using TeyvatLens.Core.Geometry;

namespace TeyvatLens.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void Distance_ReturnsEuclideanDistance()
    {
        Assert.Equal(5, GeoMath.Distance(0, 0, 3, 4), 6);
    }

    [Theory]
    [InlineData(0, -10, 0)]
    [InlineData(10, 0, 90)]
    [InlineData(0, 10, 180)]
    [InlineData(-10, 0, 270)]
    public void Bearing_UsesNorthUpClockwiseConvention(double x, double z, double expected)
    {
        Assert.Equal(expected, GeoMath.BearingDeg(0, 0, x, z), 6);
    }

    [Theory]
    [InlineData(10, 350, 20)]
    [InlineData(350, 10, -20)]
    [InlineData(180, 0, 180)]
    public void AngleDelta_ReturnsShortestSignedDifference(double target, double heading, double expected)
    {
        Assert.Equal(expected, GeoMath.AngleDelta(target, heading), 6);
    }

    [Theory]
    [InlineData(0, "прямо")]
    [InlineData(40, "справа")]
    [InlineData(-40, "слева")]
    [InlineData(170, "позади")]
    public void DirectionLabel_DescribesRelativeBearing(double angle, string expected)
    {
        Assert.Equal(expected, GeoMath.DirectionLabel(angle));
    }

    [Fact]
    public void HeightHint_UsesThreeMeterThreshold()
    {
        Assert.Equal("на одном уровне", GeoMath.HeightHint(2.9, 0));
        Assert.Equal("цель выше", GeoMath.HeightHint(5, 0));
        Assert.Equal("цель ниже", GeoMath.HeightHint(-5, 0));
        Assert.Equal("высота неизвестна", GeoMath.HeightHint(null, 0));
    }
}
