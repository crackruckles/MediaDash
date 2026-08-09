"""Tests for pipeline helpers and output structure."""

import json
import sys
from pathlib import Path

import pandas as pd
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from etl.pipeline import _build_geojson, _infer_region, _census_as_annual


def _make_sal_gdf():
    import geopandas as gpd
    from shapely.geometry import box
    return gpd.GeoDataFrame(
        {
            "SAL_NAME_2021": ["Bunbury", "Mandurah", "Rockingham"],
            "SAL_CODE_2021": ["SAL1001", "SAL1002", "SAL1003"],
        },
        geometry=[
            box(115.6, -33.3, 115.7, -33.2),
            box(115.7, -32.5, 115.8, -32.4),
            box(115.7, -32.3, 115.8, -32.2),
        ],
        crs="EPSG:4326",
    )


def _make_spine():
    return pd.DataFrame({
        "SAL_NAME_2021": ["Bunbury", "Mandurah", "Rockingham"],
        "SAL_CODE_2021": ["SAL1001", "SAL1002", "SAL1003"],
        "pop": [30000, 25000, 45000],
        "beds": [3.2, 3.0, 3.1],
        "growth": [0.08, 0.12, 0.15],
        "crime": [45.2, 38.1, 42.0],
        "trend": [-5.0, 2.3, -1.1],
        "median": [450000, 380000, 420000],
        "icsea": [1010, 990, 1005],
        "school": ["Bunbury SHS", "Mandurah SHS", "Rockingham SHS"],
        "amen": [25, 18, 30],
        "confidence": [False, False, False],
    })


def test_build_geojson_structure():
    sal_gdf = _make_sal_gdf()
    spine = _make_spine()
    result = _build_geojson(sal_gdf, spine)

    assert result["type"] == "FeatureCollection"
    assert len(result["features"]) == 3

    feature = result["features"][0]
    assert feature["type"] == "Feature"
    assert "geometry" in feature
    assert "properties" in feature
    props = feature["properties"]
    assert "id" in props
    assert "name" in props
    assert "region" in props
    assert "pop" in props
    assert "crime" in props
    assert "median" in props


def test_build_geojson_serialisable():
    sal_gdf = _make_sal_gdf()
    spine = _make_spine()
    result = _build_geojson(sal_gdf, spine)
    json_str = json.dumps(result)
    assert len(json_str) > 0


def test_infer_region():
    assert _infer_region("Bunbury") == "Bunbury–Busselton"
    assert _infer_region("Mandurah") == "Mandurah–Pinjarra"
    assert _infer_region("Rockingham") == "Perth South"
    assert _infer_region("Armadale") == "Perth South"


def test_census_as_annual():
    spine = pd.DataFrame({
        "SAL_NAME_2021": ["Bunbury", "Mandurah"],
        "pop": [30000, None],
    })
    result = _census_as_annual(spine)
    assert len(result) == 7  # 2019-2025 for Bunbury only
    assert result["SAL_NAME_2021"].unique().tolist() == ["Bunbury"]
    assert set(result["year"]) == set(range(2019, 2026))
