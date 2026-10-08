#!/usr/bin/env python3
"""Publish a prepared news import without exposing the admin key or following redirects."""
import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class ImportFailure(Exception):
    """A safe, actionable error that contains no request or credential data."""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", help="Prepared import JSON file (or - for stdin)")
    parser.add_argument("--dry-run", action="store_true", help="Check JSON without contacting the API")
    args = parser.parse_args()
    raw = sys.stdin.buffer.read(32769) if args.file == "-" else open(args.file, "rb").read(32769)
    if len(raw) > 32768:
        raise ValueError("Import exceeds 32 KiB")
    data = json.loads(raw)
    if not isinstance(data, dict) or set(data) != {"article", "expectedRevision"}:
        raise ValueError("Provide article and expectedRevision")
    if args.dry_run:
        print("JSON is ready for server validation; no request sent.")
        return
    origin = os.environ.get("FUEL_NEWS_API_URL", "")
    key = os.environ.get("FUEL_NEWS_ADMIN_KEY", "")
    url = urllib.parse.urlsplit(origin)
    if url.scheme != "https" or not url.hostname or url.username or url.password or url.query or url.fragment or url.path not in ("", "/"):
        raise ValueError("Set FUEL_NEWS_API_URL to the direct HTTPS backend origin")
    if not key:
        raise ValueError("Set FUEL_NEWS_ADMIN_KEY in the runner's secret environment")
    request = urllib.request.Request(origin.rstrip("/") + "/admin/fuel-news/import", data=raw,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json", "Accept": "application/json"}, method="POST")
    opener = urllib.request.build_opener(NoRedirect())
    try:
        with opener.open(request, timeout=30) as response:
            result = json.load(response)
        item = result["data"]["item"]
        print(json.dumps({"status": result["data"]["status"], "id": item["id"], "revision": item["revision"]}))
    except urllib.error.HTTPError as error:
        # Do not echo response bodies or request headers that might contain credentials.
        raise ImportFailure(f"Import rejected (HTTP {error.code}); inspect the article and current revision before retrying") from None
    except urllib.error.URLError:
        raise ImportFailure("Backend unavailable; retry the exact same prepared import later") from None


if __name__ == "__main__":
    try:
        main()
    except ImportFailure as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except (ValueError, OSError, KeyError):
        # Explicit safe messages; never include connection strings, tokens, or file contents.
        print("News import failed. Check JSON, runner configuration, backend availability, and current revision.", file=sys.stderr)
        sys.exit(1)
