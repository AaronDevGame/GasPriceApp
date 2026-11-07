"""DOE task HTTP client. Reads a separate env file; never prints credentials."""
import argparse
import json
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
PREFIX = '/admin/fuel-prices/doe/import/extracted'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # Never forward the shared secret to a redirected host.


def configuration(env_file):
    config = {}
    path = Path(env_file)
    if path.exists():
        for line in path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            name, separator, value = line.partition('=')
            if not separator or name not in ('CODEX_ADMIN_API_KEY', 'DOE_API_BASE_URL'):
                raise ValueError('Task env file must contain only DOE_API_BASE_URL and CODEX_ADMIN_API_KEY.')
            config[name] = value.strip().strip('\"\'')
    for name in ('CODEX_ADMIN_API_KEY', 'DOE_API_BASE_URL'):
        if name in os.environ:
            config[name] = os.environ[name]
    base = config.get('DOE_API_BASE_URL', '').rstrip('/')
    parsed = urllib.parse.urlsplit(base)
    if (parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password
            or parsed.path or parsed.query or parsed.fragment):
        raise ValueError('DOE_API_BASE_URL must be an HTTPS origin with no credentials or path.')
    key = config.get('CODEX_ADMIN_API_KEY', '').strip()
    if not key or key.startswith('replace-'):
        raise ValueError('Configure CODEX_ADMIN_API_KEY in the private task environment first.')
    if any(ord(char) < 33 or ord(char) > 126 for char in key):
        raise ValueError('CODEX_ADMIN_API_KEY must contain only printable non-space ASCII characters.')
    return base, key


def request(base, key, path, payload=None):
    body = json.dumps(payload, allow_nan=False).encode() if payload is not None else None
    req = urllib.request.Request(base + path, data=body, headers={
        'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
    opener = urllib.request.build_opener(NoRedirect())
    # Retry only a definite rate-limit rejection, never an uncertain POST outcome.
    for attempt in range(4):
        try:
            with opener.open(req, timeout=60) as response:
                return json.load(response)['data']
        except urllib.error.HTTPError as error:
            if error.code == 429 and attempt < 3:
                delay = error.headers.get('Retry-After', '2')
                delay = int(delay) if delay.isdigit() else 2
                if delay > 60:
                    raise RuntimeError('Rate limited for over 60 seconds; try on the next run.') from None
                time.sleep(max(2, delay))
                continue
            raise RuntimeError(f'Backend HTTP {error.code}; request was not confirmed successful.') from None
        except (urllib.error.URLError, TimeoutError):
            raise RuntimeError('Network outcome uncertain. Do not blindly resubmit; check saved reports/jobs first.') from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--env-file', default=str(ROOT / '.env.doe-task'))
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('preflight')
    check = commands.add_parser('check')
    check.add_argument('source_url')
    submit = commands.add_parser('submit')
    submit.add_argument('payload_file')
    submit.add_argument('--receipt', required=True, help='New private file to store the accepted job before polling')
    poll = commands.add_parser('poll')
    poll.add_argument('job_id', type=uuid.UUID)
    args = parser.parse_args()
    base, key = configuration(args.env_file)
    if args.command == 'preflight':
        print('DOE task environment is configured; deployment/authentication still require an API check.')
        return
    if args.command == 'check':
        result = request(base, key, PREFIX + '/reports?' + urllib.parse.urlencode({'sourceUrl': args.source_url}))
        print(json.dumps(result))
        return
    if args.command == 'submit':
        payload = json.loads(Path(args.payload_file).read_text())
        # Reserve the receipt before sending: an existing receipt blocks an accidental repeat.
        with open(args.receipt, 'x') as receipt:
            os.chmod(args.receipt, 0o600)
            json.dump({'status': 'submission_pending', 'sourceUrl': payload['sourceUrl'],
                       'weekStart': payload['weekStart'], 'weekEnd': payload['weekEnd']}, receipt)
        result = request(base, key, PREFIX, payload)
        Path(args.receipt).write_text(json.dumps(result))
        print(json.dumps(result), flush=True)
        job_id = uuid.UUID(result['jobId'])
    else:
        job_id = args.job_id
    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        time.sleep(3)
        result = request(base, key, PREFIX + '/jobs/' + str(job_id))
        if result['status'] in ('completed', 'completed_with_gaps', 'failed', 'interrupted'):
            print(json.dumps(result))
            if result['status'] != 'completed':
                raise RuntimeError('Import did not fully complete; inspect the returned result and report gaps/failure.')
            return
    raise RuntimeError('Polling timed out; resume with poll JOB_ID. Do not resubmit.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, RuntimeError) as error:
        # Error details may contain local paths but never request headers or credentials.
        print(str(error), file=sys.stderr)
        sys.exit(1)
