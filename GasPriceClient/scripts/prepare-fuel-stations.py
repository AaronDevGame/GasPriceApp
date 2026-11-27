"""Extract fuel points and station footprints from Geofabrik Philippine traffic shapefiles.

Run with Python, pyshp and shapely, passing the extracted shapefile directory.
The output database is © OpenStreetMap contributors, licensed under ODbL 1.0.
"""
import json
import sys
from pathlib import Path

import shapefile
from shapely.geometry import shape

source = Path(sys.argv[1])
stations = []
for layer in ("traffic", "traffic_a"):
    with shapefile.Reader(str(source / f"gis_osm_{layer}_free_1.shp")) as records:
        for item in records.iterShapeRecords():
            if item.record.fclass != "fuel":
                continue
            point = shape(item.shape.__geo_interface__).representative_point()
            stations.append(dict(id=f"{layer}/{item.record.osm_id}", name=item.record.name or "Fuel station",
                                 lat=round(point.y, 6), lng=round(point.x, 6)))
if not stations:
    raise ValueError("No fuel stations found")
target = Path(__file__).resolve().parents[1] / "src/map/data/fuel-stations.json"
target.write_text("[\n" + ",\n".join(json.dumps(s, ensure_ascii=False) for s in stations) + "\n]\n")
