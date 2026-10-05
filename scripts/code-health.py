#!/usr/bin/env python3
"""Repeatable D17 capture and compact review without an LLM. See #1938, #1945."""
from __future__ import annotations

import argparse
from datetime import date
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile

_spec = importlib.util.spec_from_file_location('code_health_review', Path(__file__).with_name('code-health-review.py'))
review = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(review)


ROOT = Path(__file__).resolve().parents[1]


def encoded(value):
    return (json.dumps(value, sort_keys=True, indent=2) + '\n').encode()


def run(command, output=None, allowed=(0,)):
    result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE)
    if result.returncode not in allowed:
        sys.stderr.buffer.write(result.stdout + result.stderr)
        raise RuntimeError(f'Collector failed ({result.returncode}): {command}')
    if output:
        output.write_bytes(result.stdout)
    else:
        sys.stderr.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    return result.returncode


def workspace_identity():
    # Capture all source/configuration inputs, including untracked MSBuild sources.
    # Baseline artifacts, corpora and build outputs are not source inputs.
    tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).split(b'\0')
    files = subprocess.check_output(['git', 'ls-files', '-z', '--cached', '--others',
                                     '--exclude-standard'], cwd=ROOT).split(b'\0')
    rows = []
    for raw in sorted(set(files) - {b''}):
        path = Path(raw.decode())
        if set(path.parts).intersection({'bin', 'obj', 'test-pdfs', 'third_party', '.claude'}):
            continue
        if path.parts[:3] == ('tests', 'code-health', 'baselines'):
            continue
        if path.name != '.editorconfig' and path.suffix.lower() not in {'.cs', '.csproj', '.props', '.targets', '.sln',
                '.slnx', '.axaml', '.xaml', '.json', '.py', '.sh', '.editorconfig', '.config', '.resx'}:
            continue
        source = ROOT / path
        rows.append({'path': path.as_posix(), 'tracked': raw in tracked,
                     'sha256': hashlib.sha256(source.read_bytes()).hexdigest() if source.is_file() else None})
    return {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip(),
            'sdk': subprocess.check_output(['dotnet', '--version'], cwd=ROOT).decode().strip(),
            'inputs': rows}


def external_directory(path):
    path = path.resolve()
    if path == ROOT or ROOT in path.parents:
        raise ValueError('Capture/review output must be outside the checkout; baseline promotion is a separate review.')
    if path.exists() and any(path.iterdir()):
        raise ValueError('Output directory must be empty; existing evidence is never overwritten.')
    path.mkdir(parents=True, exist_ok=True)
    return path


def compact_snapshot(reports, limit):
    shipping = [r for r in reports['structural']['Metrics']
                if r.get('Classification') == 'shipping' and r.get('Kind') in ('method', 'local-function')]
    metrics = {}
    for name in ('Cyclomatic', 'Cognitive', 'MaxNesting', 'ExecutableLines', 'ParameterCount'):
        metrics[name] = [{'symbol': row['SymbolKey'], 'file': row['Path'],
                          'line': row['StartLine'], 'value': row[name]}
                         for row in sorted(shipping, key=lambda r: (-r[name], r['SymbolKey'], r['Path'], r['StartLine']))[:limit]]
    return {'schemaVersion': 1, 'scope': 'shipping callable review candidates',
            'counts': {'inventorySymbols': len(reports['inventory']['Symbols']),
                       'shippingCallables': len(shipping),
                       'duplicateGroups': len(reports['duplication']['Candidates']),
                       'diagnostics': reports['diagnostics']['totalFindings']},
            'largestByMetric': metrics,
            'duplicateCandidates': [{
                'id': row['Id'], 'kind': row['Kind'], 'match': row['Match'],
                'reviewStatus': row['ReviewStatus'], 'occurrenceCount': len(row['Occurrences']),
                'examples': [{name: occurrence['Source'][name]
                              for name in ('SymbolKey', 'Path', 'StartLine', 'Classification')}
                             for occurrence in row['Occurrences'][:3]],
                'fullEvidence': 'duplication.json',
            } for row in reports['duplication']['Candidates'][:limit]],
            'entrypointReviewCandidates': {name: value[:limit] if isinstance(value, list) else value
                for name, value in reports['dependencies'].get('boundaryReviewCandidates', {}).items()},
            'unknownTestEvidence': {name: reports['testStrength'].get(name)
                                    for name in ('Execution', 'Coverage', 'UnresolvedTestInvocationCount')},
            'policy': 'Separate metric rankings, not a composite score, defect verdict, deletion proof or release gate.'}


def capture(output, reviewer, as_of, limit):
    output = external_directory(output)
    before = workspace_identity()
    commands = [
        ['dotnet', 'run', '--project', 'tools/Excise.CodeHealth', '--', 'inventory', str(ROOT), str(output / 'inventory.json')],
        ['dotnet', 'run', '--project', 'tools/Excise.StructuralEvidence', '--', 'capture', str(ROOT), str(output / 'structural.json')],
        ['dotnet', 'run', '--project', 'tools/Excise.DuplicateEvidence', '--', 'capture', str(ROOT), str(output / 'duplication.json'), as_of.isoformat()],
        [sys.executable, 'scripts/code-health-dependencies.py', 'capture', str(output / 'dependencies.json')],
        ['dotnet', 'run', '--project', 'tools/Excise.TestEvidence', '--', 'capture', str(ROOT), str(output / 'inventory.json'), str(output / 'testStrength.json')],
    ]
    for command in commands:
        print('Collecting ' + ' '.join(command), file=sys.stderr, flush=True)
        run(command)
    run([sys.executable, 'scripts/code-health-diagnostics.py', 'capture', '--root', str(ROOT),
         '--inventory', str(output / 'inventory.json')], output / 'diagnostics.json')
    if before != workspace_identity():
        raise RuntimeError('Source/configuration/tool inputs changed during capture. Partial files are not a valid snapshot.')
    run([sys.executable, 'scripts/code-health-review.py', 'bundle', '--directory', str(output),
         '--reviewer', reviewer], output / 'snapshot.json')
    for level in ('project', 'component'):
        run([sys.executable, 'scripts/code-health-dependencies.py', 'graph', str(output / 'dependencies.json'),
             str(output / (level + '-dependencies.dot')), '--level', level])
    run([sys.executable, 'scripts/code-health-dependencies.py', 'entrypoints', str(output / 'dependencies.json'),
         str(output / 'entrypoints.json')])
    _, reports = review.load_bundle(output / 'snapshot.json')
    (output / 'summary.json').write_bytes(encoded(compact_snapshot(reports, limit)))
    status = subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT).decode()
    (output / 'capture.json').write_bytes(encoded({'schemaVersion': 1, 'asOf': as_of.isoformat(),
        'identity': before, 'treeState': 'dirty-diagnostic' if status else 'clean-candidate',
        'snapshotSha256': review.digest((output / 'snapshot.json').read_bytes()),
        'policy': 'Clean-candidate means source capture only, not reviewed baseline or release approval.'}))
    if before != workspace_identity():
        raise RuntimeError('Inputs changed while finalizing capture; evidence must not be promoted.')
    print(str(output / 'summary.json'))


def compare(before, after, output, as_of, limit):
    output = external_directory(output)
    code = run([sys.executable, 'scripts/code-health-review.py', 'report', '--before', str(before.resolve()),
                '--after', str(after.resolve()), '--as-of', as_of.isoformat()], output / 'review.json', (0, 1))
    report = json.loads((output / 'review.json').read_bytes())
    required = [row for row in report['reviews'] if row['status'] == 'increased-review-required']
    (output / 'summary.json').write_bytes(encoded({'schemaVersion': 1,
        'changedSymbolCount': len(report['changedSymbols']), 'reviewRequiredCount': len(required),
        'reviewRequired': required[:limit], 'truncated': len(required) > limit,
        'expiredExceptions': report['expiredExceptions'], 'fullReport': 'review.json',
        'policy': report['policy']}))
    print(str(output / 'summary.json'))
    return code


def self_test(collectors=False):
    # Pure output contract tests; collectors own source-specific planted cases.
    data = {'inventory': {'Symbols': []}, 'structural': {'Metrics': [
        {'Classification': 'shipping', 'Kind': 'method', 'SymbolKey': 'B', 'Path': 'b.cs', 'StartLine': 1,
         'Cyclomatic': 2, 'Cognitive': 2, 'MaxNesting': 1, 'ExecutableLines': 3, 'ParameterCount': 0},
        {'Classification': 'test', 'Kind': 'method', 'SymbolKey': 'Test', 'Path': 't.cs', 'StartLine': 1,
         'Cyclomatic': 99, 'Cognitive': 99, 'MaxNesting': 99, 'ExecutableLines': 99, 'ParameterCount': 99}]},
        'duplication': {'Candidates': []}, 'diagnostics': {'totalFindings': 0},
        'dependencies': {'boundaryReviewCandidates': {'contract': 'candidate only', 'componentEdges': [1, 2]}}, 'testStrength': {}}
    result = compact_snapshot(data, 1)
    assert result['counts']['shippingCallables'] == 1
    assert result['largestByMetric']['Cyclomatic'][0]['symbol'] == 'B'
    assert result['entrypointReviewCandidates']['componentEdges'] == [1]
    assert encoded(result) == encoded(compact_snapshot(data, 1))
    try:
        external_directory(ROOT / 'artifacts/code-health-test')
        raise AssertionError('Checkout output accepted')
    except ValueError:
        pass
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory); (path / 'evidence').write_text('preserved')
        try:
            external_directory(path)
            raise AssertionError('Evidence overwrite accepted')
        except ValueError:
            pass
    review.self_test()
    print('PASS: compact deterministic separate metrics, shipping scope, immutable external evidence destinations.')
    if collectors:
        for command in (
            ['dotnet', 'run', '--project', 'tools/Excise.CodeHealth', '--', 'self-test', str(ROOT)],
            ['dotnet', 'run', '--project', 'tools/Excise.StructuralEvidence', '--', 'self-test'],
            ['dotnet', 'run', '--project', 'tools/Excise.DuplicateEvidence', '--', 'self-test', str(ROOT)],
            ['dotnet', 'run', '--project', 'tools/Excise.TestEvidence', '--', 'self-test'],
            ['dotnet', 'run', '--project', 'tools/Excise.Reachability', '--', '--self-test'],
            [sys.executable, 'scripts/code-health-dependencies.py', 'self-test'],
            [sys.executable, 'scripts/code-health-diagnostics.py', 'self-test'],
            [sys.executable, 'scripts/code-health-mutation-pilot.py', '--self-test'],
        ):
            run(command)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('capture', 'review', 'self-test'))
    parser.add_argument('--output', type=Path)
    parser.add_argument('--reviewer')
    parser.add_argument('--as-of', type=date.fromisoformat)
    parser.add_argument('--before', type=Path); parser.add_argument('--after', type=Path)
    parser.add_argument('--limit', type=int, default=20)
    parser.add_argument('--collectors', action='store_true', help='also run planted collector regressions in self-test mode')
    args = parser.parse_args()
    if args.limit < 1:
        parser.error('--limit must be positive')
    if args.mode == 'self-test':
        self_test(args.collectors); return 0
    if args.collectors:
        parser.error('--collectors applies only to self-test')
    if not args.output or not args.as_of:
        parser.error('capture/review requires --output and explicit --as-of')
    if args.mode == 'capture':
        if not args.reviewer or not args.reviewer.strip():
            parser.error('capture requires a named --reviewer')
        capture(args.output, args.reviewer, args.as_of, args.limit); return 0
    if not args.before or not args.after:
        parser.error('review requires --before and --after snapshot manifests')
    return compare(args.before, args.after, args.output, args.as_of, args.limit)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.CalledProcessError) as error:
        print('ERROR: ' + str(error), file=sys.stderr); sys.exit(2)
