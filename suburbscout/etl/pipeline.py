"""§9 — Main ETL pipeline: joins all sources into suburbs.json.

Pipeline order (from the spec):
1. Load corridor SAL polygons (§1) — the spine
2. Left-join Census fields (§2): pop, beds
3. Compute apportioned annual pop and growth (§3)
4. Ingest crime series (§4), derive crime rate and trend
5. Scrape/attach median price (§5)
6. Nearest-secondary icsea + school (§6)
7. Count amenities per polygon (§7)
8. Set confidence = pop < 8000
9. Write suburbs.json
"""

import json
import logging
import sys
from pathlib import Path

import geopandas as gpd
import pandas as pd

from .config import OUTPUT_DIR, SMALL_POP_THRESHOLD

logger = logging.getLogger(__name__)


class PipelineConfig:
    """Paths to input data files. None = auto-detect from data/ dir."""

    def __init__(self, **kwargs):
        self.sal_geojson = kwargs.get("sal_geojson")
        self.sal_local_file = kwargs.get("sal_local_file")
        self.census_g01 = kwargs.get("census_g01")
        self.census_g02 = kwargs.get("census_g02")
        self.sa2_erp = kwargs.get("sa2_erp")
        self.sal_sa2_correspondence = kwargs.get("sal_sa2_correspondence")
        self.crime_data = kwargs.get("crime_data")
        self.price_data = kwargs.get("price_data")
        self.school_locations = kwargs.get("school_locations")
        self.icsea_data = kwargs.get("icsea_data")
        self.scrape_prices = kwargs.get("scrape_prices", False)
        self.fetch_amenities_live = kwargs.get("fetch_amenities_live", True)
        self.use_cache = kwargs.get("use_cache", True)
        self.output_path = kwargs.get("output_path", OUTPUT_DIR / "suburbs.json")


def run(config=None):
    """Run the full ETL pipeline and write suburbs.json."""
    if config is None:
        config = PipelineConfig()

    from . import geography, census, population, crime, prices, schools, amenities, names

    steps_completed = []
    sal_gdf = None
    spine = None

    # --- Step 1: Base geography ---
    logger.info("=== Step 1: Loading corridor SAL polygons ===")
    try:
        if config.sal_local_file:
            sal_gdf = geography.load_from_local_file(config.sal_local_file)
        else:
            sal_gdf = geography.load_corridor_sal(use_cache=config.use_cache)
        spine = sal_gdf[["SAL_NAME_2021", "SAL_CODE_2021"]].copy()
        steps_completed.append("geography")
        logger.info("Spine: %d suburbs", len(spine))
    except Exception as e:
        logger.error("Failed to load geography: %s", e)
        raise

    alias_table = names.build_alias_table(spine["SAL_NAME_2021"].tolist())
    logger.info("Built alias table with %d entries", len(alias_table))

    # --- Step 2: Census (pop, beds) ---
    logger.info("=== Step 2: Census data (pop, beds) ===")
    try:
        census_df = census.load_census_data(config.census_g01, config.census_g02)
        spine = spine.merge(
            census_df[["SAL_NAME_2021", "census_pop", "beds"]],
            on="SAL_NAME_2021",
            how="left",
        )
        spine = spine.rename(columns={"census_pop": "pop"})
        steps_completed.append("census")
    except FileNotFoundError:
        logger.warning("Census data not found — pop and beds will be null")
        spine["pop"] = None
        spine["beds"] = None

    # --- Step 3: Annual population & growth ---
    logger.info("=== Step 3: SA2 ERP (annual pop, growth) ===")
    annual_pop = None
    try:
        erp_df = population.load_sa2_erp(config.sa2_erp)
        corr_df = population.load_sal_sa2_correspondence(config.sal_sa2_correspondence)

        census_for_weights = spine[["SAL_NAME_2021", "pop"]].rename(columns={"pop": "census_pop"})
        census_for_weights["census_pop"] = census_for_weights["census_pop"].fillna(0)

        weights_df = population.build_weights(census_for_weights, corr_df)
        annual_pop = population.apportion_annual_pop(weights_df, erp_df)

        latest_pop = annual_pop.loc[
            annual_pop.groupby("SAL_NAME_2021")["year"].idxmax()
        ][["SAL_NAME_2021", "pop"]].rename(columns={"pop": "pop_erp"})
        spine = spine.merge(latest_pop, on="SAL_NAME_2021", how="left")
        spine["pop"] = spine["pop_erp"].combine_first(spine["pop"])
        spine = spine.drop(columns=["pop_erp"])

        growth_sa2 = population.compute_growth(erp_df)
        growth_sal = population.compute_suburb_growth(weights_df, growth_sa2)
        spine = spine.merge(growth_sal, on="SAL_NAME_2021", how="left")
        steps_completed.append("population")
    except FileNotFoundError:
        logger.warning("ERP/correspondence data not found — growth will be null")
        spine["growth"] = None

    # --- Step 4: Crime ---
    logger.info("=== Step 4: Crime rates ===")
    try:
        crime_df = crime.load_crime_data(config.crime_data)

        if annual_pop is not None:
            pop_for_crime = annual_pop
        else:
            pop_for_crime = _census_as_annual(spine)

        crime_rates = crime.compute_crime_rates(crime_df, pop_for_crime, alias_table)
        spine = spine.merge(crime_rates, on="SAL_NAME_2021", how="left")
        steps_completed.append("crime")
    except FileNotFoundError:
        logger.warning("Crime data not found — crime and trend will be null")
        spine["crime"] = None
        spine["trend"] = None

    # --- Step 5: Median price ---
    logger.info("=== Step 5: Median house price ===")
    try:
        if config.scrape_prices:
            price_df = prices.scrape_reiwa_prices(spine["SAL_NAME_2021"].tolist())
        else:
            price_df = prices.load_price_data(config.price_data)

        price_joined = prices.join_prices(price_df, alias_table)
        spine = spine.merge(price_joined, on="SAL_NAME_2021", how="left")
        steps_completed.append("prices")
    except FileNotFoundError:
        logger.warning("Price data not found — median will be null")
        spine["median"] = None

    # --- Step 6: Schools / ICSEA ---
    logger.info("=== Step 6: Schools & ICSEA ===")
    try:
        school_locs = schools.load_school_locations(config.school_locations)
        try:
            icsea_df = schools.load_icsea(config.icsea_data)
        except FileNotFoundError:
            logger.warning("ICSEA data file not found — will use school locations without ICSEA values")
            icsea_df = None

        school_result = schools.find_nearest_secondary(sal_gdf, school_locs, icsea_df)
        spine = spine.merge(school_result, on="SAL_NAME_2021", how="left")
        steps_completed.append("schools")
    except FileNotFoundError:
        logger.warning("School data not found — icsea and school will be null")
        spine["school"] = None
        spine["icsea"] = None

    # --- Step 7: Amenities ---
    logger.info("=== Step 7: Amenities (OSM) ===")
    try:
        amenity_pois = amenities.fetch_amenities(use_cache=config.use_cache)
        amenity_counts = amenities.count_amenities_per_suburb(sal_gdf, amenity_pois)
        spine = spine.merge(
            amenity_counts[["SAL_NAME_2021", "amen"]],
            on="SAL_NAME_2021",
            how="left",
        )
        spine["amen"] = spine["amen"].fillna(0).astype(int)
        steps_completed.append("amenities")
    except Exception as e:
        logger.warning("Amenity fetch failed: %s", e)
        spine["amen"] = None

    # --- Step 8: Confidence flag ---
    logger.info("=== Step 8: Confidence flag ===")
    spine["confidence"] = spine["pop"].apply(
        lambda p: p is not None and p < SMALL_POP_THRESHOLD
        if pd.notna(p) else True
    )

    # --- Step 9: Write suburbs.json ---
    logger.info("=== Step 9: Writing suburbs.json ===")
    output = _build_geojson(sal_gdf, spine)

    output_path = Path(config.output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w") as f:
        json.dump(output, f, ensure_ascii=False)

    file_size = output_path.stat().st_size
    logger.info(
        "Wrote %s (%d suburbs, %.1f KB). Steps completed: %s",
        output_path,
        len(spine),
        file_size / 1024,
        ", ".join(steps_completed),
    )

    return output_path


def _build_geojson(sal_gdf, spine):
    """Merge properties with simplified geometry into a GeoJSON FeatureCollection."""
    merged = sal_gdf[["SAL_NAME_2021", "geometry"]].merge(
        spine, on="SAL_NAME_2021", how="inner"
    )

    features = []
    for _, row in merged.iterrows():
        geom = row.geometry.__geo_interface__

        props = {
            "id": row.get("SAL_CODE_2021", row["SAL_NAME_2021"]),
            "name": row["SAL_NAME_2021"],
            "region": _infer_region(row["SAL_NAME_2021"]),
        }

        for field in ("median", "crime", "trend", "icsea", "school",
                       "amen", "beds", "pop", "growth", "confidence"):
            val = row.get(field)
            if pd.isna(val):
                props[field] = None
            elif isinstance(val, (int, float)):
                props[field] = round(val, 2) if isinstance(val, float) else int(val)
            else:
                props[field] = val

        features.append({
            "type": "Feature",
            "geometry": geom,
            "properties": props,
        })

    return {
        "type": "FeatureCollection",
        "features": features,
    }


def _infer_region(sal_name):
    """Rough region bucketing for the corridor."""
    south_suburbs = {
        "Bunbury", "South Bunbury", "East Bunbury", "Carey Park",
        "Withers", "Usher", "Eaton", "Australind", "Dalyellup",
        "Busselton", "West Busselton", "Vasse", "Dunsborough",
        "Capel", "Donnybrook", "Collie", "Harvey",
    }
    mandurah_suburbs = {
        "Mandurah", "Greenfields", "Halls Head", "Falcon",
        "Dawesville", "Pinjarra", "Ravenswood", "Meadow Springs",
        "Lakelands", "Madora Bay", "San Remo",
    }

    if sal_name in south_suburbs:
        return "Bunbury–Busselton"
    if sal_name in mandurah_suburbs:
        return "Mandurah–Pinjarra"
    return "Perth South"


def _census_as_annual(spine):
    """Fallback: use Census 2021 pop as if it were annual for all years."""
    rows = []
    for _, r in spine.iterrows():
        if pd.notna(r.get("pop")):
            for year in range(2019, 2026):
                rows.append({
                    "SAL_NAME_2021": r["SAL_NAME_2021"],
                    "year": year,
                    "pop": int(r["pop"]),
                })
    return pd.DataFrame(rows)
