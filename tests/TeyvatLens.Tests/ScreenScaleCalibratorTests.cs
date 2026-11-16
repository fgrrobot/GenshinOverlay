using TeyvatLens.Core.App;

namespace TeyvatLens.Tests;

public sealed class ScreenScaleCalibratorTests
{
    [Fact]
    public void Calibrate_UpdatesScaleFromPathRatio()
    {
        // Одометрия насчитала 100 м, а телепорты разнесены на 50 м:
        // масштаб занижен вдвое, значит px/м нужно удвоить.
        var result = ScreenScaleCalibrator.Calibrate(
            pxPerMeter: 2.0,
            odomDx: 100,
            odomDz: 0,
            atlasDx: 50,
            atlasDz: 0);

        Assert.True(result.Applied);
        Assert.Equal(4.0, result.PxPerMeter, 6);
        Assert.Equal(2.0, result.Ratio, 6);
        Assert.Contains("откалиброван", result.Message);
    }

    [Fact]
    public void Calibrate_KeepsScaleWhenPathMatches()
    {
        var result = ScreenScaleCalibrator.Calibrate(2.0, 80, 0, 80, 0);

        Assert.False(result.Applied);
        Assert.Equal(2.0, result.PxPerMeter, 6);
    }

    [Fact]
    public void Calibrate_SkipsShortTravel()
    {
        // Меньше 25 м по одометрии — слишком шумно для калибровки.
        var result = ScreenScaleCalibrator.Calibrate(2.0, 10, 0, 5, 0);

        Assert.False(result.Applied);
        Assert.Equal(2.0, result.PxPerMeter, 6);
        Assert.Empty(result.Message);
    }

    [Fact]
    public void Calibrate_RejectsInconsistentDirection()
    {
        var result = ScreenScaleCalibrator.Calibrate(2.0, 100, 0, -50, 0);

        Assert.False(result.Applied);
        Assert.Contains("направление", result.Message);
    }

    [Fact]
    public void Calibrate_RejectsWildRatio()
    {
        var result = ScreenScaleCalibrator.Calibrate(2.0, 200, 0, 40, 0);

        Assert.False(result.Applied);
        Assert.Contains("расходится", result.Message);
    }

    [Fact]
    public void Calibrate_IgnoresSmallChange()
    {
        // 1 % — это шум измерения, масштаб не трогаем.
        var result = ScreenScaleCalibrator.Calibrate(2.0, 50.5, 0, 50, 0);

        Assert.False(result.Applied);
        Assert.Equal(2.0, result.PxPerMeter, 6);
    }

    [Fact]
    public void Calibrate_WorksWithDiagonalMovement()
    {
        // 30 м на северо-восток: 30/√2 по каждой оси.
        var component = 30.0 / Math.Sqrt(2.0);
        var result = ScreenScaleCalibrator.Calibrate(1.5, component * 2, -component * 2, component, -component);

        Assert.True(result.Applied);
        Assert.Equal(3.0, result.PxPerMeter, 6);
    }
}
