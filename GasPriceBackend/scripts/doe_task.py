"""DOE task HTTP client. Reads a separate env file; never prints credentials."""
import argparse
import hashlib
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
STATE = ROOT / 'GasPriceBackend' / '.doe-task-state'


def private_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(path.name + '.tmp')
    with open(temporary, 'w', opener=lambda name, flags: os.open(name, flags, 0o600)) as output:
        json.dump(value, output, indent=2, allow_nan=False)
    os.replace(temporary, path)


def review_path(report):
    identity = '\n'.join(report[k] for k in ('sourceUrl', 'weekStart', 'weekEnd'))
    return STATE / 'report-reviews' / (hashlib.sha256(identity.encode()).hexdigest() + '.json')


def unresolved(review):
    return [issue for issue in review.get('rowErrors', []) if issue['status'] == 'unresolved']


def prepare_review(payload, supplied=None):
    path = review_path(payload)
    existing = json.loads(path.read_text()) if path.exists() else None
    review = json.loads(Path(supplied).read_text()) if supplied else existing
    if review is None:
        return None
    for field in ('sourceUrl', 'weekStart', 'weekEnd'):
        if review[field] != payload[field]:
            raise ValueError('Review identity must match the submitted PDF and coverage.')
    issues = review['rowErrors']
    if not isinstance(issues, list):
        raise ValueError('Review rowErrors must be an array.')
    identifiers = set()
    for issue in issues:
        if (not isinstance(issue.get('id'), str) or not issue['id'].strip()
                or issue['id'] in identifiers or issue.get('status') not in ('unresolved', 'resolved')
                or type(issue.get('pageNumber')) is not int or issue['pageNumber'] < 1
                or not isinstance(issue.get('printedValues'), list) or not issue['printedValues']):
            raise ValueError('Review cells require unique ids, status, PDF page and printedValues.')
        identifiers.add(issue['id'])
        for field in ('province', 'oilCompany', 'fuelGrade', 'error'):
            if not isinstance(issue.get(field), str) or not issue[field].strip():
                raise ValueError('Review cells require province, company, fuel grade and error.')
        if not isinstance(issue.get('city', ''), str):
            raise ValueError('Review city must be a string (empty for a province-only block).')
        if issue['status'] == 'resolved' and not issue.get('resolution'):
            raise ValueError('Resolved cells require an evidence-based resolution note.')
    if existing and not {i['id'] for i in existing['rowErrors']} <= identifiers:
        raise ValueError('Retain prior review cells; resolve them explicitly instead of deleting them.')
    if existing:
        by_id = {issue['id']: issue for issue in issues}
        for prior in existing['rowErrors']:
            if any(prior.get(field) != by_id[prior['id']].get(field) for field in
                   ('pageNumber', 'province', 'city', 'oilCompany', 'fuelGrade', 'printedValues')):
                raise ValueError('Retain original review-cell identity and printed evidence.')
    pending = unresolved(review)
    if pending and payload['complete'] is not False:
        raise ValueError('Unresolved source cells require complete: false.')
    def row_key(row):
        return tuple(''.join(char for char in str(row.get(k) or '').casefold() if char.isalnum())
                     for k in ('province', 'city', 'oilCompany', 'fuelGrade'))
    if {row_key(i) for i in pending} & {row_key(row) for row in payload['rows']}:
        raise ValueError('An unresolved source cell must be omitted from submitted price rows.')
    private_json(path, review)
    return review


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
    submit.add_argument('--review-file', help='Private source-cell review JSON, retained across runs')
    poll = commands.add_parser('poll')
    poll.add_argument('job_id', type=uuid.UUID)
    poll.add_argument('--receipt', help='Existing accepted receipt; retain terminal status and review summary')
    args = parser.parse_args()
    base, key = configuration(args.env_file)
    if args.command == 'preflight':
        print('DOE task environment is configured; deployment/authentication still require an API check.')
        return
    if args.command == 'check':
        result = request(base, key, PREFIX + '/reports?' + urllib.parse.urlencode({'sourceUrl': args.source_url}))
        result['localReviews'] = [json.loads(path.read_text())
                                  for path in (STATE / 'report-reviews').glob('*.json')
                                  if json.loads(path.read_text()).get('sourceUrl') == args.source_url]
        for report in result['reports']:
            path = review_path({'sourceUrl': args.source_url, **report})
            review = json.loads(path.read_text()) if path.exists() else {'rowErrors': []}
            report['needsReview'] = len(unresolved(review))
            report['canSkip'] = (report['status'] == 'imported' and report['priceRows'] > 0
                                 and report['needsReview'] == 0)
        print(json.dumps(result))
        return
    if args.command == 'submit':
        payload = json.loads(Path(args.payload_file).read_text())
        if Path(args.receipt).exists():
            raise ValueError('Receipt already exists; resume its job instead of resubmitting.')
        review = prepare_review(payload, args.review_file)
        if review is not None:
            private_json(Path(args.receipt + '.review.json'), review)
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
        review = None
        if args.receipt:
            accepted = json.loads(Path(args.receipt).read_text())
            if str(job_id) != accepted.get('jobId'):
                raise ValueError('The receipt does not match the job being resumed.')
            sidecar = Path(args.receipt + '.review.json')
            review = json.loads(sidecar.read_text()) if sidecar.exists() else None
            payload = review or accepted
    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        time.sleep(3)
        result = request(base, key, PREFIX + '/jobs/' + str(job_id))
        if result['status'] in ('completed', 'completed_with_gaps', 'failed', 'interrupted'):
            # Preserve the backend's actual rowErrors; omitted source cells are separate local evidence.
            if args.receipt:
                private_json(Path(args.receipt + '.terminal.json'), result)
                private_json(Path(args.receipt + '.summary.json'), {
                    'sourceUrl': payload.get('sourceUrl') or (result.get('request') or {}).get('sourceUrl'),
                    'weekStart': payload.get('weekStart'), 'weekEnd': payload.get('weekEnd'),
                    'jobId': str(job_id), 'status': result['status'],
                    'added': result.get('priceRowsAdded', 0), 'updated': result.get('priceRowsUpdated', 0),
                    'needsReview': len(unresolved(review)) if review else 0,
                    'rowErrors': unresolved(review) if review else [],
                    'backendRowErrors': (result.get('result') or {}).get('rowErrors', [])})
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
