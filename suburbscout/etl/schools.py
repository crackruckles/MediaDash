"""§6 — Schools (ICSEA + nearest secondary) from ACARA.

Loads Australian Schools List for locations, attaches ICSEA, then finds
the nearest secondary school per SAL centroid.
"""

import logging
from pathlib import Path

import pandas as pd
from shapely.geometry import Point

from .config import DATA_DIR

logger = logging.getLogger(__name__)


def load_school_locations(path=None):
    """Load ACARA Australian Schools List.

    Expects CSV with columns for school name, latitude, longitude,
    sector, and school type.

    Returns: DataFrame with school_name, lat, lon, sector, school_type.
    """
    path = _resolve_path(path, "schools", ["*school*list*", "*acara*school*", "*schools*"])
    logger.info("Loading school locations from %s", path)
    df = pd.read_csv(path)

    name_col = _find_col(df, ["School_Name", "School Name", "school_name", "SCHOOL_NAME", "SchoolName"])
    lat_col = _find_col(df, ["Latitude", "latitude", "Lat", "lat", "Y"])
    lon_col = _find_col(df, ["Longitude", "longitude", "Lon", "lon", "Long", "X"])
    type_col = _find_col(df, ["School_Type", "School Type", "school_type", "Type"])
    sector_col = _find_col(df, ["Sector", "sector", "School_Sector"])

    if name_col is None or lat_col is None or lon_col is None:
        raise ValueError(f"Cannot find required school columns. Columns: {list(df.columns)}")

    result = pd.DataFrame({
        "school_name": df[name_col].astype(str).str.strip(),
        "lat": pd.to_numeric(df[lat_col], errors="coerce"),
        "lon": pd.to_numeric(df[lon_col], errors="coerce"),
        "school_type": df[type_col].astype(str).str.strip() if type_col else "Unknown",
        "sector": df[sector_col].astype(str).str.strip() if sector_col else "Unknown",
    })
    result = result.dropna(subset=["lat", "lon"])
    logger.info("Loaded %d schools with coordinates", len(result))
    return result


def load_icsea(path=None):
    """Load ICSEA values per school.

    Returns: DataFrame with school_name, icsea.
    """
    path = _resolve_path(path, "icsea", ["*icsea*", "*ICSEA*", "*myschool*"])
    logger.info("Loading ICSEA data from %s", path)
    df = pd.read_csv(path)

    name_col = _find_col(df, ["School_Name", "School Name", "school_name", "SCHOOL_NAME"])
    icsea_col = _find_col(df, ["ICSEA", "icsea", "ICSEA_Value", "icsea_value"])

    if name_col is None or icsea_col is None:
        raise ValueError(f"Cannot find school name/ICSEA columns. Columns: {list(df.columns)}")

    result = pd.DataFrame({
        "school_name": df[name_col].astype(str).str.strip(),
        "icsea": pd.to_numeric(df[icsea_col], errors="coerce"),
    })
    return result.dropna(subset=["icsea"])


def find_nearest_secondary(sal_gdf, schools_df, icsea_df=None):
    """For each SAL centroid, find the nearest secondary (or combined) school.

    Args:
        sal_gdf: GeoDataFrame with SAL_NAME_2021 and geometry.
        schools_df: DataFrame with school_name, lat, lon, school_type.
        icsea_df: Optional DataFrame with school_name, icsea.

    Returns: DataFrame with SAL_NAME_2021, school (name), icsea.
    """
    secondary = schools_df[
        schools_df["school_type"].str.lower().str.contains("secondary|combined|senior|high", na=False)
    ].copy()

    if secondary.empty:
        logger.warning("No secondary schools found — using all schools")
        secondary = schools_df.copy()

    if icsea_df is not None:
        secondary = secondary.merge(icsea_df, on="school_name", how="left")
    else:
        secondary["icsea"] = None

    logger.info("Finding nearest secondary for %d suburbs from %d schools", len(sal_gdf), len(secondary))

    school_points = [
        Point(row["lon"], row["lat"]) for _, row in secondary.iterrows()
    ]

    results = []
    for _, sal_row in sal_gdf.iterrows():
        centroid = sal_row.geometry.centroid
        min_dist = float("inf")
        nearest_idx = None

        for i, pt in enumerate(school_points):
            dist = centroid.distance(pt)
            if dist < min_dist:
                min_dist = dist
                nearest_idx = i

        if nearest_idx is not None:
            school_row = secondary.iloc[nearest_idx]
            results.append({
                "SAL_NAME_2021": sal_row["SAL_NAME_2021"],
                "school": school_row["school_name"],
                "icsea": school_row.get("icsea"),
            })
        else:
            results.append({
                "SAL_NAME_2021": sal_row["SAL_NAME_2021"],
                "school": None,
                "icsea": None,
            })

    result_df = pd.DataFrame(results)
    matched = result_df["icsea"].notna().sum()
    logger.info("Matched %d/%d suburbs to a school with ICSEA", matched, len(result_df))
    return result_df


def _resolve_path(path, hint, patterns=None):
    if path is not None:
        return Path(path)
    if patterns is None:
        patterns = [f"*{hint}*"]
    for pat in patterns:
        candidates = list(DATA_DIR.glob(pat + ".csv")) + \
                     list(DATA_DIR.glob(pat + ".xlsx"))
        if candidates:
            return candidates[0]
    raise FileNotFoundError(
        f"No {hint} file found in {DATA_DIR}. Place it there or pass the path."
    )


def _find_col(df, candidates):
    cols_lower = {c.lower().strip(): c for c in df.columns}
    for c in candidates:
        if c in df.columns:
            return c
        if c.lower().strip() in cols_lower:
            return cols_lower[c.lower().strip()]
    return None
