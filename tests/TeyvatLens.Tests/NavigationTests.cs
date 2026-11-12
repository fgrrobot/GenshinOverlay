using TeyvatLens.Core.Models;
using TeyvatLens.Core.Navigation;

namespace TeyvatLens.Tests;

public sealed class NavigationTests
{
    private static NearbyMarker Item(MarkerKind kind, double distance, bool collected = false, double bearing = 90)
    {
        var marker = new Marker(
            1,
            kind,
            "Тест",
            0,
            0,
            0,
            0,
            null,
            collected,
            null);
        return new NearbyMarker(marker, distance, bearing);
    }

    [Fact]
    public void Rank_PrefersChestOverSlightlyCloserFlower()
    {
        var chest = Item(MarkerKind.Chest, 20);
        var flower = Item(MarkerKind.Flower, 18);

        var ranked = HintBuilder.Rank(new[] { flower, chest });

        Assert.Equal(MarkerKind.Chest, ranked[0].Marker.Kind);
    }

    [Fact]
    public void BuildHint_IgnoresCollectedAndWaypoints()
    {
        var markers = new[]
        {
            Item(MarkerKind.Chest, 5, collected: true),
            Item(MarkerKind.Waypoint, 6),
            Item(MarkerKind.Ore, 30)
        };

        var hint = HintBuilder.BuildHint(markers, 0, 0);

        Assert.NotNull(hint);
        Assert.Equal(MarkerKind.Ore, hint!.Target.Marker.Kind);
    }

    [Fact]
    public void BuildHint_ReturnsNullWhenNothingToCollect()
    {
        var hint = HintBuilder.BuildHint(
            new[] { Item(MarkerKind.Chest, 5, collected: true) },
            0,
            0);

        Assert.Null(hint);
    }

    [Fact]
    public void BuildHint_ComputesRelativeDirection()
    {
        var hint = HintBuilder.BuildHint(
            new[] { Item(MarkerKind.Chest, 10, bearing: 90) },
            headingDeg: 0,
            playerY: 0);

        Assert.NotNull(hint);
        Assert.Equal(90, hint!.RelativeBearingDeg, 6);
        Assert.Equal("справа", hint.DirectionRu);
    }
}
