using TeyvatLens.Core.Models;
using TeyvatLens.Core.RealMap.GenshinMap;

namespace TeyvatLens.Tests;

public sealed class GenshinMapTests
{
    private const string InfoJson =
        """
        {
          "retcode": 0,
          "data": {
            "info": {
              "id": 2,
              "name": "Интерактивная карта Тейвата",
              "detail_v2": {
                "total_size": [36864, 32768],
                "padding": [7625, 6881],
                "origin": [24207, 16411],
                "map_version": "bd70cde160dbd523926208d9fa8961a1",
                "min_zoom": -4,
                "max_zoom": 0,
                "original_map_size": [21613, 19006]
              }
            }
          }
        }
        """;

    private const string TreeJson =
        """
        {
          "data": {
            "tree": [
              {
                "id": 1, "name": "Телепорты", "parent_id": 0,
                "children": [
                  { "id": 2, "name": "Статуя Семи", "parent_id": 1 },
                  { "id": 3, "name": "Точка телепорта", "parent_id": 1 }
                ]
              },
              {
                "id": 13, "name": "Сундуки", "parent_id": 0,
                "children": [
                  { "id": 17, "name": "Обычный сундук", "parent_id": 13 }
                ]
              }
            ]
          }
        }
        """;

    private const string AnchorsJson =
        """
        {
          "data": {
            "list": [
              {
                "name": "Монштадт", "l_x": 0, "l_y": 0, "r_x": 1000, "r_y": 1000,
                "children": [
                  { "name": "Город", "l_x": 100, "l_y": 100, "r_x": 200, "r_y": 200, "children": [] }
                ]
              }
            ]
          }
        }
        """;

    private static readonly GenshinMapInfo Info = new(
        MapId: 2,
        Name: "Тестовая карта",
        Version: "v1",
        OriginX: 1000,
        OriginY: 2000,
        TotalWidth: 3000,
        TotalHeight: 3000,
        MinZoom: -4,
        MaxZoom: 0);

    [Fact]
    public void ParseInfo_ReadsDetailV2()
    {
        var info = GenshinMapJson.ParseInfo(InfoJson);

        Assert.Equal(2, info.MapId);
        Assert.Equal("bd70cde160dbd523926208d9fa8961a1", info.Version);
        Assert.Equal(24207, info.OriginX);
        Assert.Equal(16411, info.OriginY);
        Assert.Equal(36864, info.TotalWidth);
        Assert.Equal(32768, info.TotalHeight);
        Assert.Equal(-4, info.MinZoom);
        Assert.Equal(0, info.MaxZoom);
    }

    [Fact]
    public void ParseLabelTree_FlattensChildrenIntoCategories()
    {
        var labels = GenshinMapJson.ParseLabelTree(TreeJson);

        Assert.Equal(5, labels.Count);
        Assert.Equal(1, labels[3].CategoryId);
        Assert.Equal("Точка телепорта", labels[3].Name);
        Assert.Equal(13, labels[17].CategoryId);
    }

    [Fact]
    public void ParseAnchors_FlattensNestedRegions()
    {
        var anchors = GenshinMapJson.ParseAnchors(AnchorsJson);

        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, anchor => anchor.Name == "Город");
        Assert.True(anchors.Single(anchor => anchor.Name == "Город").Area < 100000);
    }

    [Fact]
    public void ParsePoints_ReadsCoordinatesAndState()
    {
        const string json =
            """
            {
              "data": {
                "point_list": [
                  { "id": 1, "label_id": 3, "x_pos": -12.5, "y_pos": 40, "display_state": 1 },
                  { "id": 2, "label_id": 17, "x_pos": 10, "y_pos": 20 }
                ]
              }
            }
            """;

        var points = GenshinMapJson.ParsePoints(json);

        Assert.Equal(2, points.Count);
        Assert.Equal(-12.5, points[0].X, 3);
        Assert.Equal(40, points[0].Y, 3);
        Assert.Equal(1, points[1].DisplayState);
    }

    [Fact]
    public void Convert_MapsCategoriesCoordinatesAndSkipsJunk()
    {
        var points = new[]
        {
            new GenshinPoint(1, 3, 10, 20, 1),
            new GenshinPoint(2, 17, 500, 600, 1),
            new GenshinPoint(3, 17, 500, 600, 1),
            new GenshinPoint(4, 50, 300, 300, 1),
            new GenshinPoint(5, 3, 30, 40, 0)
        };

        var labels = new Dictionary<int, GenshinLabel>
        {
            [1] = new(1, "Телепорты", 0, 1),
            [3] = new(3, "Точка телепорта", 1, 1),
            [13] = new(13, "Сундуки", 0, 13),
            [17] = new(17, "Обычный сундук", 13, 13),
            [50] = new(50, "Враги", 0, 50)
        };

        var result = GenshinMapImporter.Convert(points, labels, Array.Empty<GenshinAnchor>(), Info);

        Assert.Equal(2, result.Total);
        Assert.Equal(MarkerKind.Waypoint, result.Markers[0].Kind);
        Assert.Equal(1010.0, result.Markers[0].X, 3);
        Assert.Equal(2020.0, result.Markers[0].Z, 3);
        Assert.Equal(MarkerKind.Chest, result.Markers[1].Kind);
        Assert.Contains(result.Warnings, warning => warning.Contains("Дубликаты"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Скрытые"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Категории вне прототипа"));
    }

    [Fact]
    public void Convert_NamesPointBySmallestContainingRegion()
    {
        var points = new[] { new GenshinPoint(1, 3, 150, 150, 1) };
        var labels = new Dictionary<int, GenshinLabel>
        {
            [3] = new(3, "Точка телепорта", 1, 1)
        };
        var anchors = new[]
        {
            new GenshinAnchor("Монштадт", 0, 0, 1000, 1000),
            new GenshinAnchor("Город", 100, 100, 200, 200)
        };

        var result = GenshinMapImporter.Convert(points, labels, anchors, Info);

        Assert.Equal("Точка телепорта · Город", Assert.Single(result.Markers).Name);
    }

    [Fact]
    public void Convert_RespectsCategoryOptions()
    {
        var points = new[]
        {
            new GenshinPoint(1, 3, 10, 20, 1),
            new GenshinPoint(2, 17, 500, 600, 1)
        };
        var labels = new Dictionary<int, GenshinLabel>
        {
            [3] = new(3, "Точка телепорта", 1, 1),
            [17] = new(17, "Обычный сундук", 13, 13)
        };

        var result = GenshinMapImporter.Convert(
            points,
            labels,
            Array.Empty<GenshinAnchor>(),
            Info,
            new GenshinImportOptions(IncludeChests: false));

        Assert.Equal(MarkerKind.Waypoint, Assert.Single(result.Markers).Kind);
    }

    [Fact]
    public void TileUrl_UsesZoomSuffixAndPng()
    {
        var url = GenshinMapClient.TileUrl(Info, 76, 33, 0);

        Assert.Equal(
            "https://act-webstatic.hoyoverse.com/map_manage/map/2/v1/76_33_P0.png",
            url);

        Assert.EndsWith("/76_33_N2.png", GenshinMapClient.TileUrl(Info, 76, 33, -2));
    }

    [Fact]
    public void ComputeRegion_AlignsToTileGrid()
    {
        var (leftTile, topTile, tiles) = GenshinTileComposer.ComputeRegion(5000, 5000, 2560);

        Assert.Equal(10, tiles);
        Assert.Equal(14, leftTile);
        Assert.Equal(14, topTile);
        Assert.Equal(3584, leftTile * GenshinMapInfo.TileSize);
    }

    [Fact]
    public void ComputeRegion_RejectsNonTileMultiple()
    {
        Assert.Throws<ArgumentException>(() => GenshinTileComposer.ComputeRegion(0, 0, 1000));
    }

    [Fact]
    public void ComputeGlobalGrid_UsesNegativeZoomDivisor()
    {
        var (tilesX, tilesY, width, height) = GenshinTileComposer.ComputeGlobalGrid(Info, -3);

        Assert.Equal(2, tilesX);
        Assert.Equal(2, tilesY);
        Assert.Equal(512, width);
        Assert.Equal(512, height);
    }

    [Fact]
    public void ComputeGlobalGrid_MatchesRealMapDimensions()
    {
        var info = new GenshinMapInfo(
            MapId: 2,
            Name: "Тейват",
            Version: "v1",
            OriginX: 24207,
            OriginY: 16411,
            TotalWidth: 36864,
            TotalHeight: 32768,
            MinZoom: -4,
            MaxZoom: 0);

        var (tilesX, tilesY, width, height) = GenshinTileComposer.ComputeGlobalGrid(info, -3);

        Assert.Equal(18, tilesX);
        Assert.Equal(16, tilesY);
        Assert.Equal(4608, width);
        Assert.Equal(4096, height);
    }

    [Fact]
    public void ComputeGlobalGrid_DefaultZoomIsFourMetersPerPixel()
    {
        var info = new GenshinMapInfo(
            MapId: 2,
            Name: "Тейват",
            Version: "v1",
            OriginX: 24207,
            OriginY: 16411,
            TotalWidth: 36864,
            TotalHeight: 32768,
            MinZoom: -4,
            MaxZoom: 0);

        var (tilesX, tilesY, width, height) =
            GenshinTileComposer.ComputeGlobalGrid(info, GenshinGlobalAtlas.DefaultZoom);

        Assert.Equal(-2, GenshinGlobalAtlas.DefaultZoom);
        Assert.Equal(36, tilesX);
        Assert.Equal(32, tilesY);
        Assert.Equal(9216, width);
        Assert.Equal(8192, height);

        var atlas = new GenshinGlobalAtlas("global.png", -2, 0.25, width, height);
        Assert.Equal(4.0, atlas.MetersPerPixel, 6);
        Assert.Equal(0.25, atlas.Profile.PxPerMeter, 6);
    }

    [Fact]
    public void ComputeGlobalGrid_RejectsNonNegativeZoom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GenshinTileComposer.ComputeGlobalGrid(Info, 0));
    }

    [Fact]
    public void MapRegion_ContainsRespectsMargin()
    {
        var region = new GenshinMapRegion("map.png", 1000, 2000, 2560);

        Assert.True(region.Contains(1500, 2500));
        Assert.False(region.Contains(1050, 2500, marginM: 120));
        Assert.False(region.Contains(4000, 2500));
    }
}
