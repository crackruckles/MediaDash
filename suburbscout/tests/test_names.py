"""Tests for name normalisation."""

import pytest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from etl.names import build_alias_table, resolve, _normalise


def test_normalise_strips_and_lowercases():
    assert _normalise("  Bunbury  ") == "bunbury"
    assert _normalise("SOUTH FREMANTLE") == "south fremantle"


def test_normalise_collapses_hyphens():
    assert _normalise("Serpentine-Jarrahdale") == "serpentine jarrahdale"


def test_normalise_removes_parens():
    assert _normalise("Mandurah (Central)") == "mandurah central"


def test_build_alias_table_includes_sal_names():
    sal_names = ["Bunbury", "Mandurah", "Rockingham"]
    table = build_alias_table(sal_names)
    assert table["bunbury"] == "Bunbury"
    assert table["mandurah"] == "Mandurah"
    assert table["rockingham"] == "Rockingham"


def test_resolve_matches_case_insensitive():
    table = build_alias_table(["Bunbury", "Mandurah"])
    assert resolve("BUNBURY", table) == "Bunbury"
    assert resolve("mandurah", table) == "Mandurah"
    assert resolve("bunbury", table) == "Bunbury"


def test_resolve_returns_none_for_unknown():
    table = build_alias_table(["Bunbury"])
    assert resolve("Nonexistent Suburb", table) is None


def test_resolve_handles_hyphens():
    table = build_alias_table(["Serpentine-Jarrahdale"])
    assert resolve("Serpentine-Jarrahdale", table) == "Serpentine-Jarrahdale"
    assert resolve("serpentine jarrahdale", table) == "Serpentine-Jarrahdale"
