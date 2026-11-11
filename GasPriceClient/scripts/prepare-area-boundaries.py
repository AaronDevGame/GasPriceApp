#!/usr/bin/env python3
"""Build the selectable overview polygons from the versioned city package (Shapely 2.0.7)."""
from collections import defaultdict
import json
from pathlib import Path

from shapely import prepare, set_precision, union_all
from shapely.geometry import MultiPolygon, Point, Polygon, shape
from shapely.geometry.polygon import orient


CLIENT = Path(__file__).resolve().parents[1]
SOURCE = CLIENT.parent / "GasPriceBackend/Geography/ph-2026q3-v1"
OUTPUT = CLIENT / "src/map/data/area-boundaries.json"
# About one screen pixel or less at each level's typical maximum zoom.
TOLERANCE = {"island": 0.006, "region": 0.003, "province": 0.001}
ISLANDS = {
    "Luzon": {"01", "02", "03", "04", "05", "13", "14", "17"},
    "Visayas": {"06", "07", "08", "18"},
    "Mindanao": {"09", "10", "11", "12", "16", "19"},
}


def area_ids(city):
    region = city["regionId"]
    island = next(name for name, codes in ISLANDS.items() if region[:2] in codes)
    province = f'province:{city["provinceId"]}' if city["provinceId"] else city["id"]
    return f"island:{island}", f"region:{region}", province


def encode_ring(ring):
    # Google encoded polyline, latitude first, five decimal places.
    encoded = []
    previous = (0, 0)
    for lon, lat in ring.coords:
        current = (round(lat * 100000), round(lon * 100000))
        for start, end in zip(previous, current):
            delta = end - start
            value = 2 * delta if delta >= 0 else -2 * delta - 1
            while value >= 32:
                encoded.append(chr(63 + (value & 31) + 32))
                value >>= 5
            encoded.append(chr(63 + value))
        previous = current
    return "".join(encoded)


def parts(geometry):
    return list(geometry.geoms) if isinstance(geometry, MultiPolygon) else [geometry]


def boundary(members, level):
    tolerance = TOLERANCE[level]
    dissolved = union_all(members, grid_size=0.000001)
    if level in ("island", "region"):
        # Small mismatches between source files create long, subpixel-wide slivers.
        # Stroking those holes at overview scale would draw false internal borders.
        pieces = [Polygon(p.exterior, [ring for ring in p.interiors
                         if not Polygon(ring).buffer(-tolerance / 4).is_empty])
                  for p in parts(dissolved)]
        dissolved = union_all(pieces, grid_size=0.000001)
    simplified = set_precision(dissolved.simplify(tolerance, preserve_topology=True), 0.00001)
    # Simplifying an archipelago may place a small island inside a nearby shell.
    if isinstance(simplified, MultiPolygon) and not simplified.is_valid:
        simplified = union_all(simplified.geoms, grid_size=0.00001)
    assert isinstance(simplified, (Polygon, MultiPolygon)) and simplified.is_valid and not simplified.is_empty
    return simplified


def encode(geometry):
    # Each polygon is a shell followed by its holes, with opposite winding.
    polygons = [orient(polygon, sign=1.0) for polygon in parts(geometry)]
    return [[encode_ring(p.exterior), *(encode_ring(ring) for ring in p.interiors)] for p in polygons]


def main():
    index = json.loads((SOURCE / "index.json").read_text())
    cities = {city["id"]: city for city in index["cities"]}
    members = defaultdict(list)
    anchors = defaultdict(list)
    for entry in index["files"]:
        data = json.loads((SOURCE / (entry["id"] + ".geojson")).read_text())
        for feature in data["features"]:
            city = cities.pop(feature["id"])
            geometry = shape(feature["geometry"])
            for area_id in area_ids(city):
                members[area_id].append(geometry)
                anchors[area_id].append(Point(city["labelPoint"]))
    assert not cities, "Missing indexed cities"
    areas = {}
    for area_id in sorted(members):
        level = area_id.partition(":")[0] if ":" in area_id else "province"
        geometry = boundary(members[area_id], level)
        prepare(geometry)
        assert all(geometry.covers(point) for point in anchors[area_id]), area_id
        areas[area_id] = encode(geometry)
    package = {
        "version": index["version"],
        "attribution": index["attribution"] + " Area outlines dissolved and simplified by GasPriceApp.",
        "licenseUrl": index["licenseUrl"],
        "sourceLicense": (SOURCE.parent / "LICENSE-MIT.txt").read_text(),
        "areas": areas,
    }
    OUTPUT.parent.mkdir(exist_ok=True, parents=True)
    OUTPUT.write_text(json.dumps(package, separators=(",", ":"), allow_nan=False) + "\n")
    print(f"{len(areas)} areas, {OUTPUT.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
