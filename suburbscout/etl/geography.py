"""§1 — Base geography: ABS Suburbs & Localities (SAL).

Downloads SAL boundaries from the ABS ArcGIS REST service, filters to the
Perth–Bunbury corridor, and simplifies geometry for browser use.
"""

import json
import logging
import time

import geopandas as gpd
import requests
from shapely.geometry import box, shape

from .config import (
    ABS_SAL_ARCGIS_URL,
    CORRIDOR_BBOX,
    DATA_DIR,
    GEOMETRY_SIMPLIFY_TOLERANCE,
    USER_AGENT,
)

logger = logging.getLogger(__name__)

CACHED_GEOJSON = DATA_DIR / "sal_corridor.geojson"


def load_corridor_sal(use_cache=True):
    """Return a GeoDataFrame of SAL polygons for the corridor.

    Columns: SAL_CODE_2021, SAL_NAME_2021, geometry (simplified).
    """
    if use_cache and CACHED_GEOJSON.exists():
        logger.info("Loading cached SAL geometry from %s", CACHED_GEOJSON)
        gdf = gpd.read_file(CACHED_GEOJSON)
        return gdf

    logger.info("Fetching SAL geometry from ABS ArcGIS service")
    features = _fetch_sal_features()

    gdf = gpd.GeoDataFrame.from_features(features, crs="EPSG:4326")

    gdf = gdf.rename(columns={
        "SAL_CODE21": "SAL_CODE_2021",
        "SAL_NAME21": "SAL_NAME_2021",
    })

    for col in ("SAL_CODE_2021", "SAL_NAME_2021"):
        if col not in gdf.columns:
            alt = [c for c in gdf.columns if "SAL" in c.upper() and ("CODE" in c.upper() or "NAME" in c.upper())]
            logger.warning("Column %s not found; available SAL columns: %s", col, alt)

    corridor = box(
        CORRIDOR_BBOX["west"],
        CORRIDOR_BBOX["south"],
        CORRIDOR_BBOX["east"],
        CORRIDOR_BBOX["north"],
    )
    gdf = gdf[gdf.geometry.intersects(corridor)].copy()
    logger.info("Filtered to %d SAL polygons in corridor", len(gdf))

    gdf["geometry"] = gdf.geometry.simplify(
        GEOMETRY_SIMPLIFY_TOLERANCE, preserve_topology=True
    )

    gdf = gdf[["SAL_CODE_2021", "SAL_NAME_2021", "geometry"]]

    gdf.to_file(CACHED_GEOJSON, driver="GeoJSON")
    logger.info("Cached corridor SAL to %s", CACHED_GEOJSON)

    return gdf


def _fetch_sal_features():
    """Page through the ArcGIS REST endpoint for SAL polygons."""
    bbox = CORRIDOR_BBOX
    geometry_param = (
        f"{bbox['west']},{bbox['south']},{bbox['east']},{bbox['north']}"
    )

    all_features = []
    offset = 0
    batch = 200

    while True:
        params = {
            "where": "1=1",
            "geometry": geometry_param,
            "geometryType": "esriGeometryEnvelope",
            "spatialRel": "esriSpatialRelIntersects",
            "outFields": "*",
            "returnGeometry": "true",
            "f": "geojson",
            "resultOffset": offset,
            "resultRecordCount": batch,
            "inSR": "4326",
            "outSR": "4326",
        }
        resp = requests.get(
            ABS_SAL_ARCGIS_URL,
            params=params,
            headers={"User-Agent": USER_AGENT},
            timeout=60,
        )
        resp.raise_for_status()
        data = resp.json()

        features = data.get("features", [])
        if not features:
            break

        all_features.extend(features)
        logger.info("Fetched %d SAL features (offset=%d)", len(features), offset)

        if len(features) < batch:
            break

        offset += batch
        time.sleep(0.5)

    logger.info("Total SAL features fetched: %d", len(all_features))
    return all_features


def load_from_local_file(path):
    """Load SAL boundaries from a local GeoPackage or Shapefile.

    Use this if the ArcGIS service is down or you have the ASGS download.
    """
    gdf = gpd.read_file(path)

    name_col = next(
        (c for c in gdf.columns if "SAL_NAME" in c.upper()), None
    )
    code_col = next(
        (c for c in gdf.columns if "SAL_CODE" in c.upper()), None
    )

    if name_col:
        gdf = gdf.rename(columns={name_col: "SAL_NAME_2021"})
    if code_col:
        gdf = gdf.rename(columns={code_col: "SAL_CODE_2021"})

    corridor = box(
        CORRIDOR_BBOX["west"],
        CORRIDOR_BBOX["south"],
        CORRIDOR_BBOX["east"],
        CORRIDOR_BBOX["north"],
    )
    gdf = gdf[gdf.geometry.intersects(corridor)].copy()
    gdf["geometry"] = gdf.geometry.simplify(
        GEOMETRY_SIMPLIFY_TOLERANCE, preserve_topology=True
    )
    gdf = gdf[["SAL_CODE_2021", "SAL_NAME_2021", "geometry"]].copy()
    return gdf
