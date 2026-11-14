using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap;

namespace TeyvatLens.Tests;

public sealed class WaypointImporterTests
{
    private static readonly MapProfile Profile = new("map.png", 2.0, 100, 200);

    [Fact]
    public void Parse_PointList_ConvertsAtlasPixelsToMeters()
    {
        const string json =
            """
            {
              "units": "pixels",
              "point_list": [
                { "name": "Телепорт: Монштадт", "x": 1000, "y": 400, "icon": "UI_Icon_Teleport_Waypoint" },
                { "name": "Сундук у моста", "x": 200, "y": 100, "icon": "UI_Icon_Chest" },
                { "name": "Квест", "x": 10, "y": 10, "icon": "UI_Icon_Quest" }
              ]
            }
            """;

        var result = WaypointImporter.Parse(json, Profile);

        Assert.Equal(2, result.Total);
        Assert.Single(result.Warnings);

        var waypoint = result.Markers[0];
        Assert.Equal(MarkerKind.Waypoint, waypoint.Kind);
        Assert.Equal(600.0, waypoint.X, 3);
        Assert.Equal(400.0, waypoint.Z, 3);

        Assert.Equal(MarkerKind.Chest, result.Markers[1].Kind);
    }

    [Fact]
    public void Parse_Meters_DoesNotApplyScale()
    {
        const string json =
            """
            { "units": "meters", "points": [
                { "name": "Статуя", "x": 12.5, "y": -3, "kind": "statue" }
            ] }
            """;

        var result = WaypointImporter.Parse(json, Profile);

        var marker = Assert.Single(result.Markers);
        Assert.Equal(MarkerKind.Waypoint, marker.Kind);
        Assert.Equal(112.5, marker.X, 3);
        Assert.Equal(197.0, marker.Z, 3);
    }

    [Fact]
    public void Parse_MissingCoordinates_ProducesWarning()
    {
        const string json =
            """
            [ { "name": "Без координат", "kind": "chest" } ]
            """;

        var result = WaypointImporter.Parse(json, Profile);

        Assert.Empty(result.Markers);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void MapProfile_RoundTripsCoordinates()
    {
        var x = Profile.ToWorldX(640);
        var z = Profile.ToWorldZ(320);

        Assert.Equal(640.0, Profile.ToAtlasX(x), 6);
        Assert.Equal(320.0, Profile.ToAtlasZ(z), 6);
    }
}
