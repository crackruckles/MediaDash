"""§7 — Amenities from OpenStreetMap via Overpass API.

Queries Overpass for amenity features in the corridor bbox, then counts
(weighted) per suburb polygon using point-in-polygon.
"""

import json
import logging
import time
from pathlib import Path

import geopandas as gpd
import pandas as pd
import requests
from shapely.geometry import Point

from .config import (
    AMENITY_TAGS,
    AMENITY_WEIGHTS,
    CORRIDOR_BBOX,
    DATA_DIR,
    OVERPASS_API_URL,
    USER_AGENT,
)

logger = logging.getLogger(__name__)

CACHED_AMENITIES = DATA_DIR / "osm_amenities.json"


def fetch_amenities(use_cache=True):
    """Fetch amenity POIs from Overpass API for the corridor.

    Returns: list of dicts with lat, lon, amenity_type.
    """
    if use_cache and CACHED_AMENITIES.exists():
        logger.info("Loading cached amenities from %s", CACHED_AMENITIES)
        with open(CACHED_AMENITIES) as f:
            return json.load(f)

    bbox = CORRIDOR_BBOX
    bbox_str = f"{bbox['south']},{bbox['west']},{bbox['north']},{bbox['east']}"

    tag_queries = "\n".join(
        f'  node[{tag}]({bbox_str});' for tag in AMENITY_TAGS
    )

    query = f"""
[out:json][timeout:120];
(
{tag_queries}
);
out center;
"""

    logger.info("Querying Overpass API for corridor amenities")
    resp = requests.post(
        OVERPASS_API_URL,
        data={"data": query},
        headers={"User-Agent": USER_AGENT},
        timeout=180,
    )
    resp.raise_for_status()
    data = resp.json()

    amenities = []
    for elem in data.get("elements", []):
        lat = elem.get("lat") or elem.get("center", {}).get("lat")
        lon = elem.get("lon") or elem.get("center", {}).get("lon")
        if lat is None or lon is None:
            continue

        tags = elem.get("tags", {})
        amenity_type = _classify_amenity(tags)
        if amenity_type:
            amenities.append({
                "lat": lat,
                "lon": lon,
                "amenity_type": amenity_type,
            })

    with open(CACHED_AMENITIES, "w") as f:
        json.dump(amenities, f)
    logger.info("Fetched %d amenity POIs, cached to %s", len(amenities), CACHED_AMENITIES)
    return amenities


def count_amenities_per_suburb(sal_gdf, amenities):
    """Count weighted amenities per suburb polygon.

    Args:
        sal_gdf: GeoDataFrame with SAL_NAME_2021 and geometry.
        amenities: list of dicts with lat, lon, amenity_type.

    Returns: DataFrame with SAL_NAME_2021, amen (weighted score), amen_count (raw).
    """
    if not amenities:
        logger.warning("No amenities to count")
        return pd.DataFrame({
            "SAL_NAME_2021": sal_gdf["SAL_NAME_2021"],
            "amen": 0,
            "amen_count": 0,
        })

    points = gpd.GeoDataFrame(
        amenities,
        geometry=[Point(a["lon"], a["lat"]) for a in amenities],
        crs="EPSG:4326",
    )

    sal = sal_gdf[["SAL_NAME_2021", "geometry"]].copy()
    if sal.crs is None:
        sal = sal.set_crs("EPSG:4326")

    joined = gpd.sjoin(points, sal, how="inner", predicate="within")

    raw_counts = joined.groupby("SAL_NAME_2021").size().rename("amen_count")

    joined["weight"] = joined["amenity_type"].map(AMENITY_WEIGHTS).fillna(1)
    weighted = joined.groupby("SAL_NAME_2021")["weight"].sum().rename("amen")

    result = sal[["SAL_NAME_2021"]].merge(
        raw_counts.reset_index(), on="SAL_NAME_2021", how="left"
    ).merge(
        weighted.reset_index(), on="SAL_NAME_2021", how="left"
    )
    result["amen_count"] = result["amen_count"].fillna(0).astype(int)
    result["amen"] = result["amen"].fillna(0).round(0).astype(int)

    logger.info(
        "Amenity counts: %d suburbs scored, range %d–%d",
        len(result), result["amen"].min(), result["amen"].max(),
    )
    return result


def _classify_amenity(tags):
    """Map OSM tags to a canonical amenity type for weighting."""
    if tags.get("shop") == "supermarket":
        return "supermarket"
    if tags.get("amenity") == "hospital":
        return "hospital"
    if tags.get("amenity") == "clinic":
        return "clinic"
    if tags.get("amenity") == "pharmacy":
        return "pharmacy"
    if tags.get("public_transport") == "station":
        return "station"
    if tags.get("amenity") == "school":
        return "school"
    if tags.get("leisure") == "swimming_pool":
        return "swimming_pool"
    if tags.get("leisure") == "sports_centre":
        return "sports_centre"
    if tags.get("amenity") == "library":
        return "library"
    if tags.get("amenity") == "community_centre":
        return "community_centre"
    return None
