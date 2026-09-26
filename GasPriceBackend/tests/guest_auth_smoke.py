"""Integration checks against a disposable local database named gasprice_auth_test.

Start the API with its normal rate limits and HTTPS, then run:
  python3 GasPriceBackend/tests/guest_auth_smoke.py --ca /path/to/localhost-cert.pem
Uses PGPORT=55439 by default. Never point this at a shared or production database.
No credentials, cookie values, response bodies, or SQL results are printed.
"""
import argparse
import http.cookiejar
import json
import os
import ssl
import subprocess
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--base', default='https://localhost:58443')
parser.add_argument('--ca', required=True)
args = parser.parse_args()
assert args.base.startswith('https://localhost:'), 'Only an isolated localhost API is allowed'
context = ssl.create_default_context(cafile=args.ca)
jar = http.cookiejar.CookieJar()
browser = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(jar))
plain = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context))
last = {}


def request(path, method='GET', body=None, headers=None, cookies=False, expected=200, pace=True):
    bucket = 'login' if path.endswith('/guest/login') else path.split('?')[0].lower().rstrip('/')
    if pace:
        cooldown = 5.1 if bucket.startswith('/ai/') else 1.1
        time.sleep(max(0, cooldown - (time.monotonic() - last.get(bucket, 0))))
    last[bucket] = time.monotonic()
    data = None if body is None else json.dumps(body).encode()
    req_headers = {'Content-Type': 'application/json', **(headers or {})}
    req = urllib.request.Request(args.base + path, data=data, headers=req_headers, method=method)
    try:
        response = (browser if cookies else plain).open(req)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = json.load(response)
        assert response.status == expected, f'{method} {path}: expected {expected}, got {response.status}'
        assert payload['code'] == expected, f'{path}: envelope status mismatch'
        return payload, response.headers


def expire(app_id):
    assert str(uuid.UUID(app_id)) == app_id
    env = {**os.environ, 'PGHOST': '127.0.0.1', 'PGPORT': os.environ.get('PGPORT', '55439'), 'PGDATABASE': 'gasprice_auth_test'}
    subprocess.run(['psql', '-X', '-v', 'ON_ERROR_STOP=1', '-q', '-c',
                    f"UPDATE guests SET access_token_expires_at = now() - interval '1 second' WHERE app_instance_id = '{app_id}'"],
                   env=env, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


protected = [('/player/data', 'GET'), ('/player/data', 'PATCH'), ('/player/profile', 'PATCH'),
             ('/fuel-prices', 'GET'), ('/fuel-prices/history', 'GET'), ('/ai/chat', 'POST'), ('/ai/fuel-prices', 'POST')]
for path, method in protected:
    request(path, method, {} if method != 'GET' else None, expected=401)
print('PASS: every protected endpoint rejects anonymous requests')

native_id = str(uuid.uuid4())
native_id_header = {'X-App-Instance-Id': native_id}
login, _ = request('/auth/guest/login', 'POST', {'PlayerName': 'Smoke ' + uuid.uuid4().hex[:10]}, native_id_header)
native = login['data']
assert native['guestCredential'] and native['accessToken'] and native['appInstanceId'] == native_id
native_auth = {**native_id_header, 'Authorization': 'Bearer ' + native['accessToken']}
request('/auth/status', headers=native_auth)
request('/player/data', headers=native_auth)
payload, _ = request('/auth/guest/login', 'POST', {}, native_id_header, expected=401)
assert payload['error']['error'] == 'missing_guest_credential'
payload, _ = request('/auth/guest/login', 'POST', {}, {**native_id_header, 'X-Guest-Credential': 'x' * 43}, expected=401)
assert payload['error']['error'] == 'invalid_guest_credential'
expire(native_id)
payload, _ = request('/player/data', headers=native_auth, expected=401)
assert payload['error']['error'] == 'access_token_expired'
renewed, _ = request('/auth/guest/login', 'POST', {}, {**native_id_header, 'X-Guest-Credential': native['guestCredential']})
assert renewed['data']['playerId'] == native['playerId'] and 'guestCredential' not in renewed['data']
new_auth = {**native_id_header, 'Authorization': 'Bearer ' + renewed['data']['accessToken']}
request('/player/data', headers=native_auth, expected=401)
request('/player/data', 'PATCH', {'health': 99}, new_auth)
request('/auth/logout', 'POST', headers=new_auth)
request('/player/data', headers=new_auth, expected=401)
print('PASS: native contract, ID-only rejection, credential verification, expiry, rotation, mutation and logout')

payload, _ = request('/auth/browser/status', cookies=True)
assert payload['data']['state'] == 'new'
csrf, _ = request('/auth/browser/csrf', cookies=True)
csrf_header = {'X-CSRF-Token': csrf['data']['csrfToken'], 'Origin': args.base, 'Sec-Fetch-Site': 'same-origin'}
payload, _ = request('/auth/browser/guest/login', 'POST', {}, cookies=True, expected=400)
assert payload['error']['error'] == 'invalid_csrf_token'
login, headers = request('/auth/browser/guest/login', 'POST', {}, csrf_header, cookies=True)
assert set(login['data']) == {'state', 'playerName', 'playerId'}
browser_id = next(c.value for c in jar if c.name == 'app_instance_id')
for cookie in jar:
    attributes = {key.lower(): value for key, value in cookie._rest.items()}
    assert cookie.secure and 'httponly' in attributes
    assert attributes.get('samesite', '').lower() == 'strict'
    if cookie.name == '__Secure-gasprice_guest':
        assert cookie.path == '/auth/browser'
assert headers['Cache-Control'] == 'no-store'
reload, _ = request('/auth/browser/status', cookies=True)
assert reload['data']['playerId'] == login['data']['playerId']
request('/player/data', cookies=True)
request('/fuel-prices', cookies=True)
# A malformed bearer must never fall back to a valid ambient cookie.
request('/player/data', headers={'Authorization': 'broken', 'X-App-Instance-Id': browser_id}, cookies=True, expected=401)
for path, method in protected:
    if method in ('PATCH', 'POST'):
        result, _ = request(path, method, {}, cookies=True, expected=400)
        assert result['error']['error'] == 'invalid_csrf_token'
request('/player/data', 'PATCH', {'money': 42}, csrf_header, cookies=True)
request('/player/profile', 'PATCH', {'playerName': 'Smoke ' + uuid.uuid4().hex[:10]}, csrf_header, cookies=True)
# Validate all remaining protected routes without sending paid upstream requests.
for path in ['/ai/chat', '/ai/fuel-prices']:
    result, _ = request(path, 'POST', {}, csrf_header, cookies=True, expected=400)
    assert result['error']['error'] != 'invalid_csrf_token'
request('/fuel-prices/history', cookies=True, expected=400)
expire(browser_id)
request('/player/data', cookies=True, expected=401)
state, _ = request('/auth/browser/status', cookies=True)
assert state['data']['state'] == 'resumable'
request('/auth/browser/guest/login', 'POST', {}, csrf_header, cookies=True)
request('/player/data', cookies=True)
request('/auth/browser/logout', 'POST', cookies=True, expected=400)
request('/auth/browser/logout', 'POST', headers=csrf_header, cookies=True)
state, _ = request('/auth/browser/status', cookies=True)
assert state['data']['state'] == 'signedOut'
assert any(c.name == '__Secure-gasprice_guest' for c in jar)
assert not any(c.name == '__Host-gasprice_access' for c in jar)
request('/player/data', cookies=True, expected=401)
login2, _ = request('/auth/browser/guest/login', 'POST', {}, csrf_header, cookies=True)
assert login2['data']['playerId'] == login['data']['playerId']
print('PASS: secure scoped cookies, secret-free JSON, reload, every protected route, CSRF, expiry, retained-credential logout and explicit re-login')

# Stealing the public app ID and obtaining a CSRF token does not recover a guest.
for cookie in list(jar):
    if cookie.name in ('__Host-gasprice_access', '__Secure-gasprice_guest'):
        jar.clear(cookie.domain, cookie.path, cookie.name)
result, _ = request('/auth/browser/status', cookies=True)
assert result['data']['state'] == 'unavailable' and result['data']['playerId'] is None
request('/auth/browser/guest/login', 'POST', {}, csrf_header, cookies=True, expected=401)
request('/auth/browser/guest/login', 'POST', {}, {**csrf_header, 'Origin': 'https://attacker.invalid'}, cookies=True, expected=400)
# Native and browser logins consume the same 10-per-five-minute bucket.
result, headers = request('/auth/guest/login', 'POST', {}, {'X-App-Instance-Id': str(uuid.uuid4())}, expected=429)
assert int(headers['Retry-After']) > 0
print('PASS: public browser ID cannot recover account; foreign Origin rejected; native/browser login limits shared')
