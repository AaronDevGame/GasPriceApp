#!/usr/bin/env python3
"""Reproduce the versioned PH map package from pinned inputs (Shapely 2.0.7).
No downloads or coordinate editing. See docs/fuel-map.md for input URLs/checksums.
"""
import argparse
import csv
import gzip
import hashlib
import io
import json
from pathlib import Path
import zipfile
from shapely.geometry import shape
from shapely.geometry.polygon import orient
from shapely import to_geojson

COMMIT = '680e32e62baeccbcd8845d3b1387bc67c1baf78a'
VERSION = 'ph-2026q3-v1'
ARCHIVE_SHA = 'b78f64b04e544f8485c56bafca3b9f3936e9eae48c5c0c25ab4d811b076d17b1'
PSGC_SHA = '83cbac96063b9227994b5fd17f791a3ac9ddac76427b3b01c3fe5a2a9f2f796b'
ATTRIBUTION = 'Boundaries: NAMRIA and PSA through OCHA COD-AB (CC BY 3.0 IGO); GeoJSON: James Faeldon (MIT). Regrouped and ring orientation normalized by GasPriceApp.'
LICENSE_URL = 'https://creativecommons.org/licenses/by/3.0/igo/legalcode'

def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode() + b'\n'

def digest(data):
    return hashlib.sha256(data).hexdigest()

def code(value):
    return str(value).zfill(10)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--archive', type=Path, required=True)
    p.add_argument('--psgc', type=Path, required=True)
    p.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'Geography' / VERSION)
    a = p.parse_args()
    archive, inventory = a.archive.read_bytes(), a.psgc.read_bytes()
    assert digest(archive) == ARCHIVE_SHA, 'Boundary archive checksum mismatch'
    assert digest(inventory) == PSGC_SHA, 'PSGC inventory checksum mismatch'
    psa = {r['10-digit PSGC']: r for r in csv.DictReader(io.StringIO(inventory.decode())) if r['10-digit PSGC']}
    expected = {k for k, r in psa.items() if r['Geographic Level'] in ('City', 'Mun')}
    z = zipfile.ZipFile(io.BytesIO(archive))
    root = f'philippines-json-maps-{COMMIT}/'
    upstream = json.loads(z.read(root + 'latest/manifest.json'))
    assert upstream['psgc'] == '2026-3q'
    groups = {}
    for n in z.namelist():
        if n.startswith(root + 'latest/geojson/regions/medres/') and n.endswith('.json'):
            for f in json.loads(z.read(n))['features']:
                props = f['properties']
                groups[code(props['adm2_psgc'])] = (props['adm2_en'], props['geo_level'])
    cities, files, seen, source_hashes = [], [], set(), {}
    a.output.mkdir(parents=True, exist_ok=True)
    for n in sorted(z.namelist()):
        if not n.startswith(root + 'latest/geojson/provdists/medres/') or not n.endswith('.json'):
            continue
        raw = z.read(n)
        source_hashes[n[len(root):]] = digest(raw)
        features = json.loads(raw)['features']
        prepared = []
        for f in features:
            props = f['properties']
            cid, rid, gid = (code(props[k]) for k in ('adm3_psgc', 'adm1_psgc', 'adm2_psgc'))
            assert cid in expected and cid not in seen
            seen.add(cid)
            g = shape(f['geometry'])
            assert g.geom_type in ('Polygon', 'MultiPolygon') and not g.is_empty and g.is_valid, cid
            # Reorder ring orientation only: retain every original boundary vertex.
            if g.geom_type == 'Polygon':
                g = orient(g, sign=1.0)
            else:
                from shapely.geometry import MultiPolygon
                g = MultiPolygon([orient(poly, sign=1.0) for poly in g.geoms])
            assert g.bounds[0] > 110 and g.bounds[2] < 130 and g.bounds[1] > 0 and g.bounds[3] < 25
            anchor = g.representative_point()
            assert g.contains(anchor)
            city_class = psa[cid]['City Class'].strip() or None
            name, group_type = groups[gid]
            province = gid if group_type == 'Prov' and city_class not in ('HUC', 'ICC') else None
            entry = dict(id=cid, name=props['adm3_en'], level=props['geo_level'], cityClass=city_class,
                         regionId=rid, regionName=psa[rid]['Name'].strip(), provinceId=province,
                         groupId=gid, groupName=name, groupType=group_type,
                         bbox=list(g.bounds), labelPoint=[anchor.x, anchor.y])
            prepared.append((entry, dict(type='Feature', id=cid, properties=dict(cityId=cid),
                                        geometry=json.loads(to_geojson(g)))))
        # Bound chunks to 256 KiB uncompressed, except a single large municipality.
        batches, batch = [], []
        for item in sorted(prepared, key=lambda item: item[0]['id']):
            if batch and len(encoded([f for _, f in batch] + [item[1]])) > 256 * 1024:
                batches.append(batch)
                batch = []
            batch.append(item)
        if batch: batches.append(batch)
        for part, batch in enumerate(batches, 1):
            file_id = f'{batch[0][0]["groupId"]}-{part:02d}'
            bounds = [min(e['bbox'][0] for e, _ in batch), min(e['bbox'][1] for e, _ in batch),
                      max(e['bbox'][2] for e, _ in batch), max(e['bbox'][3] for e, _ in batch)]
            collection = dict(type='FeatureCollection', bbox=bounds, attribution=ATTRIBUTION,
                              license=LICENSE_URL, features=[f for _, f in batch])
            data = encoded(collection)
            (a.output / f'{file_id}.geojson').write_bytes(data)
            files.append(dict(id=file_id, bbox=bounds, bytes=len(data), gzipBytes=len(gzip.compress(data, mtime=0)),
                              sha256=digest(data), cityIds=[e['id'] for e, _ in batch]))
            cities.extend(dict(e, fileId=file_id) for e, _ in batch)
    assert seen == expected and len(seen) == 1642, 'Incomplete nationwide municipality coverage'
    bounds = [min(c['bbox'][0] for c in cities), min(c['bbox'][1] for c in cities),
              max(c['bbox'][2] for c in cities), max(c['bbox'][3] for c in cities)]
    index = dict(version=VERSION, countryCode='PH', psgcVintage='2026-3q', defaultCityId='1380600000',
                 bbox=bounds, attribution=ATTRIBUTION, licenseUrl=LICENSE_URL,
                 cities=sorted(cities, key=lambda c: c['id']), files=sorted(files, key=lambda f: f['id']))
    (a.output / 'index.json').write_bytes(encoded(index))
    manifest = dict(version=VERSION, sourceCommit=COMMIT, sourceArchiveSha256=ARCHIVE_SHA,
                    psgcInventorySha256=PSGC_SHA, upstream=upstream, sourceFileSha256=source_hashes,
                    cityCount=len(cities), fileCount=len(files), indexSha256=digest(encoded(index)),
                    checks='PSGC inventory equality; unique IDs; valid nonempty polygon topology; PH extent; interior anchors',
                    modifications='Preserved medium-resolution source geometry; normalized ring orientation, grouped features, added IDs and metadata.')
    (a.output / 'manifest.json').write_bytes(encoded(manifest))
    (a.output.parent / 'LICENSE-MIT.txt').write_bytes(z.read(root + 'LICENSE'))
    (a.output.parent / 'SOURCE-README.md').write_bytes(z.read(root + 'README.md'))
    print(f'Validated {len(cities)} cities in {len(files)} files; index {len(encoded(index)):,} bytes; geometry {sum(f["bytes"] for f in files):,} bytes; gzip {sum(f["gzipBytes"] for f in files):,} bytes')

if __name__ == '__main__':
    main()
