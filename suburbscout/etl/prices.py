"""§5 — House price (median) from REIWA / Landgate.

Scrapes per-suburb median house price from REIWA suburb pages, or loads
from a pre-scraped CSV. No free API exists, so this is the scrape path.
"""

import logging
import re
import time
from pathlib import Path

import pandas as pd
import requests
from bs4 import BeautifulSoup

from .config import DATA_DIR, USER_AGENT
from .names import resolve

logger = logging.getLogger(__name__)

REIWA_BASE_URL = "https://reiwa.com.au/suburb"


def load_price_data(path=None):
    """Load median prices from a pre-scraped CSV.

    Expects columns: suburb (or locality/SAL_NAME), median.
    Returns: DataFrame with suburb_raw, median.
    """
    path = _resolve_path(path)
    logger.info("Loading price data from %s", path)

    df = pd.read_csv(path)
    sub_col = _find_col(df, ["suburb", "Suburb", "locality", "SAL_NAME_2021", "SAL_NAME"])
    price_col = _find_col(df, ["median", "Median", "median_price", "Median House Price", "price"])

    if sub_col is None or price_col is None:
        raise ValueError(f"Cannot find suburb/price columns. Columns: {list(df.columns)}")

    result = df[[sub_col, price_col]].copy()
    result.columns = ["suburb_raw", "median"]
    result["median"] = pd.to_numeric(
        result["median"].astype(str).str.replace(r"[\$,kK]", "", regex=True),
        errors="coerce",
    )
    result.loc[(result["median"] > 0) & (result["median"] < 10_000), "median"] *= 1000

    logger.info("Loaded %d suburb prices", result["median"].notna().sum())
    return result


def scrape_reiwa_prices(suburb_names, delay=2.0):
    """Scrape median house price from REIWA for each suburb.

    This is slow (one request per suburb with polite delay). Results are
    saved to data/reiwa_medians.csv for caching.
    """
    results = []
    cache_path = DATA_DIR / "reiwa_medians.csv"

    for name in suburb_names:
        slug = _suburb_slug(name)
        url = f"{REIWA_BASE_URL}/{slug}/"
        try:
            resp = requests.get(
                url,
                headers={"User-Agent": USER_AGENT},
                timeout=15,
            )
            if resp.status_code == 404:
                logger.debug("REIWA 404 for %s (%s)", name, slug)
                results.append({"suburb_raw": name, "median": None})
                continue

            resp.raise_for_status()
            median = _extract_median(resp.text)
            results.append({"suburb_raw": name, "median": median})
            logger.info("REIWA %s: $%s", name, median)

        except requests.RequestException as e:
            logger.warning("REIWA request failed for %s: %s", name, e)
            results.append({"suburb_raw": name, "median": None})

        time.sleep(delay)

    df = pd.DataFrame(results)
    df.to_csv(cache_path, index=False)
    logger.info("Saved %d REIWA prices to %s", len(df), cache_path)
    return df


def join_prices(price_df, alias_table):
    """Resolve suburb names and return SAL_NAME_2021 + median."""
    price_df = price_df.copy()
    price_df["SAL_NAME_2021"] = price_df["suburb_raw"].map(
        lambda s: resolve(str(s), alias_table)
    )
    matched = price_df["SAL_NAME_2021"].notna().sum()
    logger.info("Matched %d/%d suburb prices to SAL names", matched, len(price_df))
    return price_df[["SAL_NAME_2021", "median"]].dropna(subset=["SAL_NAME_2021"])


def _extract_median(html):
    """Pull median house price from REIWA suburb page HTML."""
    soup = BeautifulSoup(html, "lxml")

    for text_node in soup.find_all(string=re.compile(r"median\s+(house\s+)?price", re.IGNORECASE)):
        parent = text_node.parent
        for sibling in parent.find_next_siblings():
            match = re.search(r"\$[\d,]+", sibling.get_text())
            if match:
                return int(match.group().replace("$", "").replace(",", ""))

    all_text = soup.get_text()
    matches = re.findall(r"median[^$]*?\$([\d,]+)", all_text, re.IGNORECASE)
    if matches:
        return int(matches[0].replace(",", ""))

    return None


def _suburb_slug(name):
    """Convert suburb name to REIWA URL slug."""
    slug = name.lower().strip()
    slug = re.sub(r"[^a-z0-9\s-]", "", slug)
    slug = re.sub(r"\s+", "-", slug)
    return slug


def _resolve_path(path):
    if path is not None:
        return Path(path)
    patterns = ["*reiwa*", "*median*", "*price*", "*landgate*"]
    for pat in patterns:
        candidates = list(DATA_DIR.glob(pat + ".csv"))
        if candidates:
            return candidates[0]
    raise FileNotFoundError(
        f"No price data file found in {DATA_DIR}. "
        f"Run scrape_reiwa_prices() first or place a CSV there."
    )


def _find_col(df, candidates):
    cols_lower = {c.lower().strip(): c for c in df.columns}
    for c in candidates:
        if c in df.columns:
            return c
        if c.lower().strip() in cols_lower:
            return cols_lower[c.lower().strip()]
    return None
