"""§4 — Crime stats from WA Police.

Ingests WA Police crime data (locality × year × offence category × count),
computes 5-year average rate per 1k residents and trend direction.
"""

import logging
from pathlib import Path

import pandas as pd

from .config import CRIME_WINDOW_YEARS, DATA_DIR
from .names import resolve

logger = logging.getLogger(__name__)


def load_crime_data(path=None):
    """Load WA Police crime data.

    Expects a CSV or Excel with columns for locality, year, offence
    category, and count. Returns a tidy long-form DataFrame:
    locality, year, offence_category, count.
    """
    path = _resolve_path(path)
    logger.info("Loading crime data from %s", path)

    if path.suffix in (".xlsx", ".xls"):
        df = pd.read_excel(path)
    else:
        df = pd.read_csv(path)

    locality_col = _find_col(df, ["Locality", "locality", "Suburb", "suburb", "Location"])
    year_col = _find_col(df, ["Year", "year", "Calendar Year", "Financial Year"])
    count_col = _find_col(df, ["Count", "count", "Offences", "offences", "Number", "Total"])

    if locality_col is None or year_col is None or count_col is None:
        raise ValueError(
            f"Cannot identify crime data columns. Columns: {list(df.columns)}"
        )

    category_col = _find_col(df, ["Offence", "offence", "Category", "category", "Offence Type"])

    cols = {"locality": locality_col, "year": year_col, "count": count_col}
    if category_col:
        cols["offence_category"] = category_col

    result = df[list(cols.values())].copy()
    result.columns = list(cols.keys())
    result["year"] = pd.to_numeric(result["year"].astype(str).str.extract(r"(\d{4})")[0], errors="coerce")
    result["count"] = pd.to_numeric(result["count"], errors="coerce").fillna(0)
    result = result.dropna(subset=["year"])
    result["year"] = result["year"].astype(int)

    logger.info("Loaded crime data: %d rows, %d localities", len(result), result["locality"].nunique())
    return result


def compute_crime_rates(crime_df, annual_pop_df, alias_table, window=CRIME_WINDOW_YEARS):
    """Compute 5-year average crime rate and trend per suburb.

    Args:
        crime_df: locality, year, count (and optionally offence_category).
        annual_pop_df: SAL_NAME_2021, year, pop.
        alias_table: name normalisation table.
        window: number of years for the averaging window.

    Returns: DataFrame with SAL_NAME_2021, crime (rate/1k/yr), trend (%).
    """
    latest_year = crime_df["year"].max()
    start_year = latest_year - window + 1
    crime_window = crime_df[crime_df["year"].between(start_year, latest_year)].copy()

    crime_window["SAL_NAME_2021"] = crime_window["locality"].map(
        lambda loc: resolve(str(loc), alias_table)
    )
    unmatched = crime_window["SAL_NAME_2021"].isna().sum()
    if unmatched > 0:
        unmatched_names = crime_window.loc[
            crime_window["SAL_NAME_2021"].isna(), "locality"
        ].unique()
        logger.warning(
            "%d crime rows unmatched (%d unique localities): %s",
            unmatched, len(unmatched_names),
            list(unmatched_names[:10]),
        )
    crime_window = crime_window.dropna(subset=["SAL_NAME_2021"])

    yearly_totals = (
        crime_window
        .groupby(["SAL_NAME_2021", "year"])["count"]
        .sum()
        .reset_index()
        .rename(columns={"count": "total_offences"})
    )

    pop_window = annual_pop_df[annual_pop_df["year"].between(start_year, latest_year)]

    merged = yearly_totals.merge(
        pop_window, on=["SAL_NAME_2021", "year"], how="left"
    )
    merged["pop"] = merged["pop"].fillna(0)
    merged["rate"] = merged.apply(
        lambda r: (r["total_offences"] / r["pop"]) * 1000 if r["pop"] > 0 else None,
        axis=1,
    )

    rates = {}
    trends = {}
    for sal, group in merged.groupby("SAL_NAME_2021"):
        rates[sal] = _safe_avg_rate(group)
        trends[sal] = _compute_trend(group)

    avg_rate = pd.DataFrame(
        [{"SAL_NAME_2021": k, "crime": v} for k, v in rates.items()]
    )
    trend = pd.DataFrame(
        [{"SAL_NAME_2021": k, "trend": v} for k, v in trends.items()]
    )

    result = avg_rate.merge(trend, on="SAL_NAME_2021", how="outer")
    result["crime"] = pd.to_numeric(result["crime"], errors="coerce").round(1)
    result["trend"] = pd.to_numeric(result["trend"], errors="coerce").round(1)

    logger.info("Computed crime rates for %d suburbs", len(result))
    return result


def _safe_avg_rate(group):
    """Average rate across years, using sum(offences)/sum(pop)*1000."""
    total_off = group["total_offences"].sum()
    total_pop = group["pop"].sum()
    if total_pop > 0:
        return (total_off / total_pop) * 1000
    return None


def _compute_trend(group):
    """Latest-year rate vs 5-yr average, as a percentage."""
    group = group.dropna(subset=["rate"]).sort_values("year")
    if len(group) < 2:
        return None
    avg = group["rate"].mean()
    latest = group.iloc[-1]["rate"]
    if avg > 0:
        return ((latest / avg) - 1) * 100
    return None


def _resolve_path(path):
    if path is not None:
        return Path(path)
    patterns = ["*crime*", "*wapol*", "*offence*", "*police*"]
    for pat in patterns:
        candidates = list(DATA_DIR.glob(pat + ".csv")) + \
                     list(DATA_DIR.glob(pat + ".xlsx"))
        if candidates:
            return candidates[0]
    raise FileNotFoundError(
        f"No crime data file found in {DATA_DIR}. Place it there or pass the path."
    )


def _find_col(df, candidates):
    cols_lower = {c.lower().strip(): c for c in df.columns}
    for c in candidates:
        if c in df.columns:
            return c
        if c.lower().strip() in cols_lower:
            return cols_lower[c.lower().strip()]
    return None
