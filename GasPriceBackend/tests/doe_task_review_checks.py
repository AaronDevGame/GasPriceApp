"""Offline regression checks for retained DOE source-cell reviews (no credentials/network)."""
import copy
import importlib.util
import json
import io
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('doe_task', Path(__file__).parents[1] / 'scripts/doe_task.py')
doe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(doe)


class ReviewChecks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.old_state = doe.STATE
        doe.STATE = Path(self.directory.name)
        self.addCleanup(setattr, doe, 'STATE', self.old_state)
        self.payload = dict(sourceUrl='https://doe.gov.ph/report.pdf', weekStart='2026-11-17',
                            weekEnd='2026-11-23', complete=False, rows=[])
        self.issue = dict(id='p8-diesel', status='unresolved', pageNumber=8, province='Pangasinan',
                          city='Lingayen', oilCompany='FLYING V', fuelGrade='DIESEL',
                          printedValues=['81.00', '0.00'], error='Invalid zero endpoint.')
        self.review = {**{k: self.payload[k] for k in ('sourceUrl', 'weekStart', 'weekEnd')},
                       'rowErrors': [self.issue]}
        self.file = doe.STATE / 'review.json'

    def save(self, review=None):
        self.file.write_text(json.dumps(review or self.review))
        return str(self.file)

    def test_reviews_survive_next_run_and_prevent_complete(self):
        doe.prepare_review(self.payload, self.save())
        self.assertEqual(len(doe.unresolved(doe.prepare_review(self.payload))), 1)
        with self.assertRaises(ValueError):
            doe.prepare_review({**self.payload, 'complete': True})
        self.assertEqual(doe.review_path(self.payload).stat().st_mode & 0o777, 0o600)

    def test_cannot_submit_affected_cell_even_with_fabricated_valid_endpoints(self):
        for company in ('FLYING V', 'FlyingV', ' flying   v '):
            self.payload['rows'] = [{**self.issue, 'oilCompany': company,
                                     'minPricePerLiter': 81, 'maxPricePerLiter': 81}]
            with self.assertRaises(ValueError):
                doe.prepare_review(self.payload, self.save())

    def test_cannot_hide_prior_issue_but_can_resolve_with_evidence(self):
        doe.prepare_review(self.payload, self.save())
        changed = copy.deepcopy(self.review)
        changed['rowErrors'] = []
        with self.assertRaises(ValueError):
            doe.prepare_review(self.payload, self.save(changed))
        changed['rowErrors'] = [{**self.issue, 'status': 'resolved', 'resolution': 'Corrected DOE PDF verified.'}]
        self.assertEqual(doe.unresolved(doe.prepare_review({**self.payload, 'complete': True}, self.save(changed))), [])

    def test_review_cannot_attach_to_another_pdf_or_coverage(self):
        for field, value in [('sourceUrl', 'https://doe.gov.ph/other.pdf'), ('weekStart', '2026-11-10')]:
            with self.assertRaises(ValueError):
                doe.prepare_review({**self.payload, field: value}, self.save())

    def test_original_printed_evidence_cannot_be_rewritten(self):
        doe.prepare_review(self.payload, self.save())
        changed = copy.deepcopy(self.review)
        changed['rowErrors'][0]['printedValues'] = ['81.00', '81.00']
        with self.assertRaises(ValueError):
            doe.prepare_review(self.payload, self.save(changed))

    def test_saved_imported_status_cannot_hide_local_unresolved_review(self):
        doe.prepare_review(self.payload, self.save())
        result = dict(sourceUrl=self.payload['sourceUrl'], reports=[dict(
            weekStart=self.payload['weekStart'], weekEnd=self.payload['weekEnd'],
            status='imported', priceRows=5)])
        output = io.StringIO()
        with patch.object(doe.sys, 'argv', ['doe_task.py', 'check', self.payload['sourceUrl']]), \
                patch.object(doe, 'configuration', return_value=('https://example.test', 'test')), \
                patch.object(doe, 'request', return_value=result), redirect_stdout(output):
            doe.main()
        report = json.loads(output.getvalue())['reports'][0]
        self.assertEqual(report['needsReview'], 1)
        self.assertFalse(report['canSkip'])

    def test_resolved_cell_requires_evidence_and_ids_are_unique(self):
        bad = copy.deepcopy(self.review)
        bad['rowErrors'][0]['status'] = 'resolved'
        with self.assertRaises(ValueError):
            doe.prepare_review(self.payload, self.save(bad))

        bad = copy.deepcopy(self.review)
        bad['rowErrors'].append(self.issue)
        with self.assertRaises(ValueError):
            doe.prepare_review(self.payload, self.save(bad))

    def test_partial_job_retains_receipt_and_review_without_retrying_post(self):
        payload = doe.STATE / 'payload.json'
        payload.write_text(json.dumps(self.payload))
        receipt = doe.STATE / 'job-receipt.json'
        job_id = '11111111-1111-4111-8111-111111111111'
        accepted = dict(jobId=job_id, status='queued')
        terminal = dict(jobId=job_id, status='completed_with_gaps', priceRowsAdded=5,
                        priceRowsUpdated=0, result=dict(status='partial', rowErrors=[]))
        argv = ['doe_task.py', 'submit', str(payload), '--receipt', str(receipt),
                '--review-file', self.save()]
        with patch.object(doe.sys, 'argv', argv), patch.object(doe, 'configuration', return_value=('https://example.test', 'test')), \
                patch.object(doe, 'request', side_effect=[accepted, terminal]) as request, \
                patch.object(doe.time, 'sleep'), redirect_stdout(io.StringIO()):
            with self.assertRaises(RuntimeError):
                doe.main()
            self.assertEqual(request.call_count, 2)  # One POST, one status read.
            with self.assertRaises(ValueError):
                doe.main()  # The same receipt never sends another POST.
            self.assertEqual(request.call_count, 2)
        self.assertEqual(json.loads(receipt.read_text()), accepted)
        self.assertEqual(json.loads(Path(str(receipt) + '.terminal.json').read_text()), terminal)
        summary = json.loads(Path(str(receipt) + '.summary.json').read_text())
        self.assertEqual(summary['needsReview'], 1)
        self.assertEqual(summary['rowErrors'][0]['printedValues'], ['81.00', '0.00'])
        self.assertEqual(summary['backendRowErrors'], [])


if __name__ == '__main__':
    unittest.main()
