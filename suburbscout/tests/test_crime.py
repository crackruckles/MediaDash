"""Tests for crime rate computation."""

import sys
from pathlib import Path

import pandas as pd
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from etl.crime import compute_crime_rates
from etl.names import build_alias_table


def test_crime_rate_basic():
    alias_table = build_alias_table(["Bunbury", "Mandurah"])

    crime_df = pd.DataFrame({
        "locality": ["Bunbury"] * 5 + ["Mandurah"] * 5,
        "year": list(range(2019, 2024)) * 2,
        "count": [100, 110, 105, 120, 115] + [80, 85, 90, 88, 92],
    })

    pop_df = pd.DataFrame({
        "SAL_NAME_2021": ["Bunbury"] * 5 + ["Mandurah"] * 5,
        "year": list(range(2019, 2024)) * 2,
        "pop": [30000] * 5 + [25000] * 5,
    })

    result = compute_crime_rates(crime_df, pop_df, alias_table, window=5)

    assert len(result) == 2
    bunbury = result[result["SAL_NAME_2021"] == "Bunbury"].iloc[0]
    assert bunbury["crime"] > 0
    assert bunbury["crime"] == pytest.approx(550 / 150000 * 1000, abs=0.5)


def test_crime_rate_zero_pop():
    alias_table = build_alias_table(["Nowhere"])
    crime_df = pd.DataFrame({
        "locality": ["Nowhere"] * 3,
        "year": [2021, 2022, 2023],
        "count": [10, 10, 10],
    })
    pop_df = pd.DataFrame({
        "SAL_NAME_2021": ["Nowhere"] * 3,
        "year": [2021, 2022, 2023],
        "pop": [0, 0, 0],
    })
    result = compute_crime_rates(crime_df, pop_df, alias_table, window=3)
    assert len(result) <= 1
    if len(result) == 1:
        assert pd.isna(result.iloc[0]["crime"]) or result.iloc[0]["crime"] is None
