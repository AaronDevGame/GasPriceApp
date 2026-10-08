#!/usr/bin/env python3
"""Isolated PostgreSQL + HTTP checks. No production DB, OpenAI calls or real pushes."""
import copy
import datetime as dt
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
UTC = dt.timezone.utc


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def run_checks(base):
    def call(path, method="GET", body=None, admin=False, headers=None, expected=200, raw=False):
        data = body if raw else json.dumps(body).encode() if body is not None else None
        request_headers = {"Accept": "application/json", **(headers or {})}
        if data is not None:
            request_headers.setdefault("Content-Type", "application/json")
        if admin:
            request_headers["Authorization"] = "Bearer news-local-admin-fixture"
        for _ in range(3):
            request = urllib.request.Request(base + path, data=data, method=method, headers=request_headers)
            try:
                response = urllib.request.urlopen(request, timeout=15)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                status = response.status
                payload = json.load(response)
                if status == 429 and expected != 429:
                    time.sleep(float(response.headers.get("Retry-After", "1")) + 0.05)
                    continue
            assert status == expected, (method, path, status, expected, payload)
            return payload["data"]
        raise AssertionError("Rate limit did not clear")

    assert call("/fuel-news/latest") is None
    assert call("/fuel-news")["items"] == []
    call("/fuel-news?unknown=1", expected=400)
    call("/fuel-news?limit=0", expected=400)
    call("/fuel-news?cursor=invalid", expected=400)
    call("/fuel-news/not-a-uuid", expected=400)
    call("/fuel-news/" + str(uuid.uuid4()), expected=404)
    call("/admin/fuel-news/import", "POST", {}, expected=401)
    call("/admin/fuel-news/import", "POST", b"{broken", admin=True, raw=True, expected=400)
    call("/admin/fuel-news/import", "POST", b"x" * 32769, admin=True, raw=True, expected=400)
    call("/admin/fuel-news/import", "POST", {}, admin=True, headers={"Content-Type": "text/plain"}, expected=400)
    print("PASS empty/public reads, bounded requests, admin auth, malformed JSON, and invalid pagination")
    now = dt.datetime.now(UTC)
    effective = (now + dt.timedelta(hours=8, days=2)).date()
    expiry = dt.datetime.combine(effective + dt.timedelta(days=1), dt.time(), UTC) - dt.timedelta(hours=8)
    article = {
        "importKey": "http-fixture-forecast", "topicKey": "http-fixture-topic", "category": "adjustment", "status": "forecast",
        "title": "Isolated fixture fuel forecast", "summary": "This fixture summary is used only by the isolated local HTTP checks.",
        "body": "This is an isolated test article. It is never imported into a production database or represented as real fuel news.",
        "effectiveDatePhilippines": effective.isoformat(), "effectiveAtUtc": None, "expiresAtUtc": expiry.isoformat().replace("+00:00", "Z"),
        "adjustments": [{"fuel": "gasoline", "oilCompany": None, "minChangePerLiter": 1, "maxChangePerLiter": 2, "status": "forecast"}],
        "sources": [{"name": "HTTP fixture source", "url": "https://example.com/fixture", "publishedAtUtc": now.isoformat().replace("+00:00", "Z")}],
    }
    payload = {"article": article, "expectedRevision": None}
    for mutate in (
        lambda a: a.update(extra=True),
        lambda a: a["adjustments"][0].pop("minChangePerLiter"),
        lambda a: a["sources"][0].update(url="javascript:alert(1)"),
        lambda a: a["sources"][0].update(url="https://localhost/fixture"),
        lambda a: a["adjustments"][0].update(minChangePerLiter=3),
        lambda a: a.update(status="confirmed"),
    ):
        invalid = copy.deepcopy(article)
        mutate(invalid)
        call("/admin/fuel-news/import", "POST", {"article": invalid, "expectedRevision": None}, admin=True, expected=400)
    duplicate = json.dumps(payload).replace('"minChangePerLiter": 1', '"minChangePerLiter": 1, "minChangePerLiter": 2')
    call("/admin/fuel-news/import", "POST", duplicate.encode(), admin=True, raw=True, expected=400)
    first = call("/admin/fuel-news/import", "POST", payload, admin=True)
    assert first["status"] == "imported"
    news_id = first["item"]["id"]
    retry = call("/admin/fuel-news/import", "POST", payload, admin=True)
    assert retry["status"] == "already_imported" and retry["item"]["updatedAtUtc"] == first["item"]["updatedAtUtc"]
    revised = copy.deepcopy(payload)
    revised["article"]["body"] += " This sentence corrects the wording."
    call("/admin/fuel-news/import", "POST", revised, admin=True, expected=409)
    revised["expectedRevision"] = 1
    assert call("/admin/fuel-news/import", "POST", revised, admin=True)["item"]["revision"] == 2
    assert len(call(f"/admin/fuel-news/{news_id}/revisions", admin=True)) == 2
    print("PASS strict nested validation, missing and duplicate properties, idempotent imports, and revision conflicts")
    confirmed = copy.deepcopy(payload)
    confirmed["article"].update(importKey="http-fixture-confirmed", status="confirmed", title="Isolated fixture confirmed adjustment")
    confirmed["article"]["adjustments"][0].update(minChangePerLiter=2, status="confirmed")
    saved = call("/admin/fuel-news/import", "POST", confirmed, admin=True)
    assert call("/fuel-news/latest")["id"] == saved["item"]["id"]
    assert call(f"/fuel-news/{news_id}")["supersededById"] == saved["item"]["id"]
    future_forecast = copy.deepcopy(payload)
    future_forecast["article"]["importKey"] = "http-fixture-later-forecast"
    call("/admin/fuel-news/import", "POST", future_forecast, admin=True, expected=400)
    page = call("/fuel-news?limit=1")
    assert len(page["items"]) == 1 and page["nextCursor"]
    next_page = call("/fuel-news?limit=1&cursor=" + urllib.parse.quote(page["nextCursor"], safe=""))
    assert next_page["items"][0]["id"] != page["items"][0]["id"] and next_page["nextCursor"] is None
    call("/fuel-news/subscription", expected=401)
    app_id = str(uuid.uuid4())
    login = call("/auth/guest/login", "POST", {}, headers={"X-App-Instance-Id": app_id})
    native = {"X-App-Instance-Id": app_id, "Authorization": "Bearer " + login["accessToken"]}
    prefs = call("/fuel-news/subscription", headers=native)
    assert prefs == {"enabled": False, "includeForecasts": False, "available": False}
    call("/fuel-news/subscription", "PUT", {"pushToken": "ExpoPushToken[fixture-device-token]", "includeForecasts": False}, headers=native, expected=503)
    assert not call("/fuel-news/subscription", "DELETE", headers=native)["enabled"]
    print("PASS confirmation supersession, latest selection, PostgreSQL cursor pagination, and authenticated notification preferences")


def main():
    pg_bin = Path(shutil.which("initdb") or "/opt/homebrew/opt/postgresql@16/bin/initdb").parent
    with tempfile.TemporaryDirectory(prefix="gasprice-news-test-") as temp:
        directory = Path(temp)
        pg_data = directory / "pg"
        pg_port, api_port = free_port(), free_port()
        subprocess.run([str(pg_bin / "initdb"), "-D", str(pg_data), "-A", "trust", "-U", "news_test"], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        started = False
        api = None
        env = os.environ.copy()
        env.pop("DATABASE_URL", None)
        connection = f"Host=127.0.0.1;Port={pg_port};Database=gasprice_news_test;Username=news_test"
        env.update(ConnectionStrings__Postgres=connection, ADMIN_API_KEY="news-local-admin-fixture", OPENAI_API_KEY="",
            ASPNETCORE_ENVIRONMENT="Production", FuelNews__PushEnabled="false", FUEL_NEWS_TEST_CONNECTION=connection,
            ASPNETCORE_URLS=f"http://127.0.0.1:{api_port}", PORT=str(api_port))
        try:
            subprocess.run([str(pg_bin / "pg_ctl"), "-D", str(pg_data), "-l", str(directory / "postgres.log"),
                "-o", f"-h 127.0.0.1 -p {pg_port} -k {temp}", "-w", "start"], check=True, stdout=subprocess.DEVNULL)
            started = True
            subprocess.run([str(pg_bin / "createdb"), "-h", "127.0.0.1", "-p", str(pg_port), "-U", "news_test", "gasprice_news_test"], check=True)
            with open(directory / "api.log", "w") as log:
                api = subprocess.Popen(["dotnet", str(ROOT / "GasPriceBackend/bin/Debug/net10.0/BackendServer.dll")],
                    cwd=ROOT / "GasPriceBackend", env=env, stdout=log, stderr=log)
                base = f"http://127.0.0.1:{api_port}"
                for _ in range(240):
                    if api.poll() is not None:
                        shutil.copyfile(directory / "api.log", "/private/tmp/gasprice-news-test-api.log")
                        raise RuntimeError("Local API stopped during startup; inspect the isolated log")
                    try:
                        urllib.request.urlopen(base + "/health", timeout=1).close()
                        break
                    except (urllib.error.URLError, TimeoutError):
                        time.sleep(0.25)
                else:
                    shutil.copyfile(directory / "api.log", "/private/tmp/gasprice-news-test-api.log")
                    raise RuntimeError("Local API failed to start")
                run_checks(base)
                subprocess.run(["dotnet", "run", "--project", "GasPriceBackend/tests/FuelNewsChecks"], cwd=ROOT, env=env, check=True)
                subprocess.run(["dotnet", "ef", "migrations", "has-pending-model-changes", "--project", "GasPriceBackend/BackendServer.csproj", "--no-build"], cwd=ROOT, env=env, check=True)
        finally:
            if api is not None and api.poll() is None:
                api.terminate()
                try:
                    api.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    api.kill()
                    api.wait()
            if started:
                subprocess.run([str(pg_bin / "pg_ctl"), "-D", str(pg_data), "-m", "fast", "-w", "stop"], stdout=subprocess.DEVNULL, check=True)


if __name__ == "__main__":
    main()
