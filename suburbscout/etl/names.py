"""Name normalisation — shared key between ABS SAL, WAPOL, and REIWA."""

import json
import logging
from pathlib import Path

from .config import DATA_DIR

logger = logging.getLogger(__name__)

ALIASES_FILE = DATA_DIR / "aliases.json"

_BUILTIN_ALIASES = {
    "CITY BEACH": "City Beach",
    "NORTH BEACH": "North Beach",
    "SOUTH FREMANTLE": "South Fremantle",
    "EAST FREMANTLE": "East Fremantle",
    "NORTH FREMANTLE": "North Fremantle",
    "WELLARD": "Wellard",
    "WEST BUSSELTON": "West Busselton",
    "PINJARRA": "Pinjarra",
    "MANDURAH": "Mandurah",
    "BUNBURY": "Bunbury",
    "BUSSELTON": "Busselton",
}


def _load_alias_file():
    if ALIASES_FILE.exists():
        with open(ALIASES_FILE) as f:
            return json.load(f)
    return {}


def build_alias_table(sal_names):
    """Build a lookup from normalised name → canonical SAL_NAME_2021.

    The canonical form is the ABS SAL name. All other sources get normalised
    to match it. The alias table (aliases.json) holds manual overrides for
    known mismatches between WAPOL / REIWA naming and ABS naming.
    """
    file_aliases = _load_alias_file()
    table = {}

    for name in sal_names:
        canonical = name.strip()
        normalised = _normalise(canonical)
        table[normalised] = canonical

    for variant, canonical in {**_BUILTIN_ALIASES, **file_aliases}.items():
        normalised = _normalise(variant)
        if normalised not in table:
            table[normalised] = canonical

    return table


def _normalise(name):
    """Lowercase, strip whitespace, collapse hyphens and parentheticals."""
    s = name.strip().lower()
    s = s.replace("-", " ").replace("(", "").replace(")", "")
    parts = s.split()
    return " ".join(parts)


def resolve(name, alias_table):
    """Resolve an external source name to the canonical SAL name, or None."""
    key = _normalise(name)
    canonical = alias_table.get(key)
    if canonical is None:
        logger.debug("No alias match for %r (normalised: %r)", name, key)
    return canonical


def save_alias_file(aliases):
    """Persist manual aliases to disk."""
    with open(ALIASES_FILE, "w") as f:
        json.dump(aliases, f, indent=2, ensure_ascii=False)
    logger.info("Wrote %d aliases to %s", len(aliases), ALIASES_FILE)
