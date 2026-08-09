"""§2 — Population & home size from ABS Census 2021 (SAL level).

Reads Census DataPack CSVs (G01 for population, G02 for median/average
dwelling stats) and joins on SAL_NAME_2021.
"""

import logging
from pathlib import Path

import pandas as pd

from .config import DATA_DIR

logger = logging.getLogger(__name__)


def load_census_g01(path=None):
    """Load Census table G01 (Selected Person Characteristics).

    Returns DataFrame with columns: SAL_NAME_2021, census_pop.
    """
    path = _resolve_path(path, "G01")
    df = pd.read_csv(path)

    sal_col = _find_column(df, ["SAL_NAME_2021", "SAL_NAME", "region_id", "SAL"])
    pop_col = _find_column(df, ["Tot_P_P", "Total_persons", "Tot_P_Tot"])

    if sal_col is None or pop_col is None:
        raise ValueError(
            f"Cannot find SAL name or population column in G01. "
            f"Columns: {list(df.columns)}"
        )

    result = df[[sal_col, pop_col]].copy()
    result.columns = ["SAL_NAME_2021", "census_pop"]
    result["SAL_NAME_2021"] = result["SAL_NAME_2021"].astype(str).str.strip()
    result["census_pop"] = pd.to_numeric(result["census_pop"], errors="coerce").fillna(0).astype(int)

    logger.info("Loaded G01: %d rows", len(result))
    return result


def load_census_g02(path=None):
    """Load Census table G02 (Selected Medians and Averages).

    Returns DataFrame with columns: SAL_NAME_2021, beds.
    """
    path = _resolve_path(path, "G02")
    df = pd.read_csv(path)

    sal_col = _find_column(df, ["SAL_NAME_2021", "SAL_NAME", "region_id", "SAL"])
    beds_col = _find_column(df, [
        "Average_num_bedrooms_per_dwelling",
        "Avg_num_bedroom",
        "Average_number_bedrooms",
        "Avg_Num_Psns_Per_Bedroom",
    ])

    if sal_col is None or beds_col is None:
        raise ValueError(
            f"Cannot find SAL name or bedrooms column in G02. "
            f"Columns: {list(df.columns)}"
        )

    result = df[[sal_col, beds_col]].copy()
    result.columns = ["SAL_NAME_2021", "beds"]
    result["SAL_NAME_2021"] = result["SAL_NAME_2021"].astype(str).str.strip()
    result["beds"] = pd.to_numeric(result["beds"], errors="coerce")

    logger.info("Loaded G02: %d rows with beds data", result["beds"].notna().sum())
    return result


def load_census_data(g01_path=None, g02_path=None):
    """Load and merge G01 + G02 into one DataFrame per SAL.

    Returns: SAL_NAME_2021, census_pop, beds.
    """
    g01 = load_census_g01(g01_path)
    g02 = load_census_g02(g02_path)
    merged = g01.merge(g02, on="SAL_NAME_2021", how="left")
    logger.info("Merged census data: %d suburbs", len(merged))
    return merged


def _resolve_path(path, table_hint):
    if path is not None:
        return Path(path)
    candidates = list(DATA_DIR.glob(f"*{table_hint}*SAL*.csv")) + \
                 list(DATA_DIR.glob(f"*{table_hint}*.csv"))
    if candidates:
        logger.info("Auto-detected %s file: %s", table_hint, candidates[0])
        return candidates[0]
    raise FileNotFoundError(
        f"No {table_hint} CSV found. Place it in {DATA_DIR} or pass the path explicitly."
    )


def _find_column(df, candidates):
    cols_lower = {c.lower(): c for c in df.columns}
    for candidate in candidates:
        if candidate in df.columns:
            return candidate
        if candidate.lower() in cols_lower:
            return cols_lower[candidate.lower()]
    return None
