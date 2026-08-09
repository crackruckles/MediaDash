"""Corridor bounds, paths, and constants for the SuburbScout ETL."""

import os
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = BASE_DIR / "data"
OUTPUT_DIR = BASE_DIR / "output"

CORRIDOR_BBOX = {
    "south": -33.65,
    "north": -31.80,
    "west": 115.40,
    "east": 116.10,
}

CORRIDOR_SA3_NAMES = {
    "Mandurah",
    "Rockingham",
    "Kwinana",
    "Armadale",
    "Serpentine - Jarrahdale",
    "Bunbury",
    "Busselton",
    "Cockburn",
    "Fremantle",
    "Melville",
    "Gosnells",
    "Canning",
}

CORRIDOR_SA4_NAMES = {
    "Perth - South West",
    "Perth - South East",
    "Mandurah",
    "Bunbury",
}

ABS_SAL_GPKG_URL = (
    "https://www.abs.gov.au/statistics/standards/australian-statistical-geography-standard-asgs-edition-3/"
    "jul2021-jun2026/access-and-downloads/digital-boundary-files"
)
ABS_SAL_ARCGIS_URL = (
    "https://geo.abs.gov.au/arcgis/rest/services/ASGS2021/SAL/MapServer/0/query"
)

ABS_DATA_API = "https://data.api.abs.gov.au"

OVERPASS_API_URL = "https://overpass-api.de/api/interpreter"

SMALL_POP_THRESHOLD = 8000

GEOMETRY_SIMPLIFY_TOLERANCE = 0.001

CRIME_WINDOW_YEARS = 5

AMENITY_TAGS = [
    '"shop"="supermarket"',
    '"leisure"="swimming_pool"',
    '"leisure"="sports_centre"',
    '"amenity"="school"',
    '"amenity"="pharmacy"',
    '"amenity"="hospital"',
    '"amenity"="clinic"',
    '"public_transport"="station"',
    '"amenity"="library"',
    '"amenity"="community_centre"',
]

AMENITY_WEIGHTS = {
    "supermarket": 3,
    "hospital": 3,
    "clinic": 2,
    "pharmacy": 2,
    "station": 3,
    "school": 2,
    "swimming_pool": 1,
    "sports_centre": 1,
    "library": 1,
    "community_centre": 1,
}

USER_AGENT = "SuburbScout-ETL/1.0 (private research tool)"

os.makedirs(DATA_DIR, exist_ok=True)
os.makedirs(OUTPUT_DIR, exist_ok=True)
