"""§3 — Annual population & growth from ABS SA2 ERP + apportionment.

Annual population doesn't exist below SA2. This module loads SA2 ERP data,
builds SAL↔SA2 correspondence weights from Census 2021 population, and
apportions annual population down to suburb level.
"""

import logging
from pathlib import Path

import pandas as pd

from .config import DATA_DIR

logger = logging.getLogger(__name__)


def load_sa2_erp(path=None):
    """Load SA2 Estimated Resident Population time series.

    Expects an Excel or CSV with columns including SA2 name/code and
    yearly population columns (or a long-format year+pop pair).

    Returns: DataFrame with SA2_NAME, year, erp.
    """
    path = _resolve_path(path, "ERP", ["*ERP*SA2*", "*SA2*ERP*", "*regional*population*"])
    logger.info("Loading SA2 ERP from %s", path)

    if path.suffix in (".xlsx", ".xls"):
        df = pd.read_excel(path, sheet_name=0, header=0)
    else:
        df = pd.read_csv(path)

    sa2_col = _find_col(df, ["SA2_NAME", "SA2_name", "sa2_name", "SA2 name", "Statistical Area Level 2"])
    if sa2_col is None:
        raise ValueError(f"Cannot find SA2 name column. Columns: {list(df.columns)}")

    year_cols = [c for c in df.columns if _is_year_col(c)]

    if year_cols:
        long = df.melt(id_vars=[sa2_col], value_vars=year_cols, var_name="year", value_name="erp")
        long = long.rename(columns={sa2_col: "SA2_NAME"})
        long["year"] = long["year"].astype(str).str.extract(r"(\d{4})").astype(int)
        long["erp"] = pd.to_numeric(long["erp"], errors="coerce")
        return long[["SA2_NAME", "year", "erp"]].dropna(subset=["erp"])

    year_col = _find_col(df, ["year", "Year", "TIME_PERIOD"])
    pop_col = _find_col(df, ["erp", "ERP", "population", "OBS_VALUE", "Value"])
    if year_col and pop_col:
        result = df[[sa2_col, year_col, pop_col]].copy()
        result.columns = ["SA2_NAME", "year", "erp"]
        result["year"] = result["year"].astype(int)
        result["erp"] = pd.to_numeric(result["erp"], errors="coerce")
        return result.dropna(subset=["erp"])

    raise ValueError("Cannot parse SA2 ERP structure. Need year columns or year+value columns.")


def load_sal_sa2_correspondence(path=None):
    """Load ABS SAL↔SA2 correspondence file.

    Returns: DataFrame with SAL_NAME_2021, SA2_NAME.
    """
    path = _resolve_path(path, "correspondence", ["*SAL*SA2*", "*correspondence*"])
    logger.info("Loading SAL-SA2 correspondence from %s", path)

    if path.suffix in (".xlsx", ".xls"):
        df = pd.read_excel(path)
    else:
        df = pd.read_csv(path)

    sal_col = _find_col(df, ["SAL_NAME_2021", "SAL_NAME", "SAL_name"])
    sa2_col = _find_col(df, ["SA2_NAME_2021", "SA2_NAME", "SA2_name"])

    if sal_col is None or sa2_col is None:
        raise ValueError(
            f"Cannot find SAL/SA2 name columns. Columns: {list(df.columns)}"
        )

    result = df[[sal_col, sa2_col]].copy()
    result.columns = ["SAL_NAME_2021", "SA2_NAME"]
    return result.drop_duplicates()


def build_weights(census_pop_df, correspondence_df):
    """Build apportionment weights: weight[SAL] = pop[SAL] / pop[parent SA2].

    Args:
        census_pop_df: DataFrame with SAL_NAME_2021, census_pop.
        correspondence_df: DataFrame with SAL_NAME_2021, SA2_NAME.

    Returns: DataFrame with SAL_NAME_2021, SA2_NAME, weight.
    """
    merged = correspondence_df.merge(census_pop_df, on="SAL_NAME_2021", how="left")
    merged["census_pop"] = merged["census_pop"].fillna(0)

    sa2_totals = merged.groupby("SA2_NAME")["census_pop"].sum().rename("sa2_total")
    merged = merged.merge(sa2_totals, on="SA2_NAME", how="left")

    merged["weight"] = merged.apply(
        lambda r: r["census_pop"] / r["sa2_total"] if r["sa2_total"] > 0 else 0,
        axis=1,
    )

    logger.info(
        "Built weights for %d SALs across %d SA2s",
        len(merged), merged["SA2_NAME"].nunique(),
    )
    return merged[["SAL_NAME_2021", "SA2_NAME", "weight"]]


def apportion_annual_pop(weights_df, erp_df):
    """Apportion SA2 ERP down to SAL level by year.

    Returns: DataFrame with SAL_NAME_2021, year, pop.
    """
    merged = weights_df.merge(erp_df, on="SA2_NAME", how="inner")
    merged["pop"] = merged["erp"] * merged["weight"]
    result = merged.groupby(["SAL_NAME_2021", "year"])["pop"].sum().reset_index()
    result["pop"] = result["pop"].round(0).astype(int)
    logger.info("Apportioned annual pop: %d suburb-year rows", len(result))
    return result


def compute_growth(erp_df, window=5):
    """Compute 5-year population growth % at SA2 level.

    Returns: DataFrame with SA2_NAME, growth.
    """
    latest_year = erp_df["year"].max()
    base_year = latest_year - window

    latest = erp_df[erp_df["year"] == latest_year][["SA2_NAME", "erp"]].rename(columns={"erp": "erp_latest"})
    base = erp_df[erp_df["year"] == base_year][["SA2_NAME", "erp"]].rename(columns={"erp": "erp_base"})

    merged = latest.merge(base, on="SA2_NAME", how="inner")
    merged["growth"] = ((merged["erp_latest"] / merged["erp_base"]) - 1).round(4)
    merged.loc[merged["erp_base"] == 0, "growth"] = None

    logger.info("Computed growth for %d SA2s (years %d–%d)", len(merged), base_year, latest_year)
    return merged[["SA2_NAME", "growth"]]


def compute_suburb_growth(weights_df, growth_df):
    """Push SA2-level growth down to SAL via the correspondence.

    Each SAL inherits its parent SA2's growth rate.
    """
    merged = weights_df[["SAL_NAME_2021", "SA2_NAME"]].drop_duplicates()
    merged = merged.merge(growth_df, on="SA2_NAME", how="left")
    result = merged.groupby("SAL_NAME_2021")["growth"].mean().reset_index()
    return result


def _resolve_path(path, hint, patterns=None):
    if path is not None:
        return Path(path)
    if patterns is None:
        patterns = [f"*{hint}*"]
    for pat in patterns:
        candidates = list(DATA_DIR.glob(pat + ".csv")) + \
                     list(DATA_DIR.glob(pat + ".xlsx")) + \
                     list(DATA_DIR.glob(pat + ".xls"))
        if candidates:
            return candidates[0]
    raise FileNotFoundError(
        f"No {hint} file found in {DATA_DIR}. Place it there or pass the path explicitly."
    )


def _find_col(df, candidates):
    cols_lower = {c.lower().strip(): c for c in df.columns}
    for c in candidates:
        if c in df.columns:
            return c
        if c.lower().strip() in cols_lower:
            return cols_lower[c.lower().strip()]
    return None


def _is_year_col(col):
    s = str(col).strip()
    if s.isdigit() and 1990 <= int(s) <= 2030:
        return True
    if s.startswith("ERP_") or s.startswith("Pop_"):
        return True
    return False
