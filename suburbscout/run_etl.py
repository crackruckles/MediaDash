#!/usr/bin/env python3
"""SuburbScout ETL — entry point.

Usage:
    python run_etl.py                    # auto-detect files in data/
    python run_etl.py --sal-local SAL.gpkg --census-g01 G01.csv ...

Data files go in suburbscout/data/. The pipeline auto-detects by filename
pattern, or you can pass explicit paths. Steps with missing data are
skipped gracefully — the output will have nulls for those fields.

Output: suburbscout/output/suburbs.json
"""

import argparse
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from etl.pipeline import PipelineConfig, run


def main():
    parser = argparse.ArgumentParser(
        description="SuburbScout ETL — build suburbs.json from free public data"
    )
    parser.add_argument("--sal-local", help="Path to local SAL GeoPackage or Shapefile")
    parser.add_argument("--census-g01", help="Path to Census G01 CSV")
    parser.add_argument("--census-g02", help="Path to Census G02 CSV")
    parser.add_argument("--sa2-erp", help="Path to SA2 ERP Excel/CSV")
    parser.add_argument("--sal-sa2-corr", help="Path to SAL↔SA2 correspondence file")
    parser.add_argument("--crime", help="Path to WA Police crime data")
    parser.add_argument("--prices", help="Path to pre-scraped price CSV")
    parser.add_argument("--scrape-prices", action="store_true",
                        help="Scrape REIWA live instead of using a CSV")
    parser.add_argument("--schools", help="Path to ACARA school locations CSV")
    parser.add_argument("--icsea", help="Path to ICSEA data CSV")
    parser.add_argument("--no-amenities", action="store_true",
                        help="Skip Overpass API amenity fetch")
    parser.add_argument("--no-cache", action="store_true",
                        help="Ignore cached data, re-fetch everything")
    parser.add_argument("--output", help="Output path (default: output/suburbs.json)")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    config = PipelineConfig(
        sal_local_file=args.sal_local,
        census_g01=args.census_g01,
        census_g02=args.census_g02,
        sa2_erp=args.sa2_erp,
        sal_sa2_correspondence=args.sal_sa2_corr,
        crime_data=args.crime,
        price_data=args.prices,
        scrape_prices=args.scrape_prices,
        school_locations=args.schools,
        icsea_data=args.icsea,
        fetch_amenities_live=not args.no_amenities,
        use_cache=not args.no_cache,
        output_path=args.output if args.output else None,
    )

    output_path = run(config)
    print(f"\nDone. Output: {output_path}")


if __name__ == "__main__":
    main()
