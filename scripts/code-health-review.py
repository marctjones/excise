#!/usr/bin/env python3
"""Immutable changed-symbol evidence and explicit review ratchet. See #1945."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import date
import hashlib
import json
from pathlib import Path
import sys

FAMILIES = ('inventory', 'structural', 'duplication', 'dependencies', 'testStrength', 'diagnostics')


def encoded(value):
    return (json.dumps(value, sort_keys=True, indent=2) + '\n').encode()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load_bundle(path):
    manifest = json.loads(path.read_bytes())
    if manifest['schemaVersion'] != 1 or set(manifest['artifacts']) != set(FAMILIES):
        raise ValueError('Snapshot requires schema 1 and all six evidence families.')
    reports = {}
    for family, artifact in sorted(manifest['artifacts'].items()):
        if artifact['path'] != family + '.json':
            raise ValueError(f'Unexpected artifact path: {family}')
        data = (path.parent / artifact['path']).read_bytes()
        if digest(data) != artifact['sha256']:
            raise ValueError(f'Artifact fingerprint mismatch: {family}')
        reports[family] = json.loads(data)
    inventory = reports['inventory']
    inventory_hash = manifest['artifacts']['inventory']['sha256']
    hashes = {r['Path']: r['SourceHash'] for r in inventory['Inputs']}
    for family in ('structural', 'duplication', 'testStrength'):
        observed = set()
        for row in reports[family]['Inputs']:
            if row['Path'] in observed:
                raise ValueError(f'Duplicate source input: {family}: {row["Path"]}')
            observed.add(row['Path'])
            if hashes.get(row['Path']) != row.get('SourceHash', row.get('Sha256')):
                raise ValueError(f'Mixed source snapshots: {family}: {row["Path"]}')
        if family in ('structural', 'duplication') and observed != set(hashes):
            raise ValueError(f'Incomplete source inventory: {family}')
    for family in FAMILIES:
        schema = reports[family].get('SchemaVersion', reports[family].get('schemaVersion'))
        if schema != 1:
            raise ValueError(f'Unsupported {family} evidence schema.')
    if reports['testStrength']['InventorySha256'] != inventory_hash:
        raise ValueError('Test evidence uses a different inventory.')
    diagnostics = reports['diagnostics']
    if diagnostics.get('inventorySha256', diagnostics.get('sourceSnapshot', {}).get('inventorySha256')) != inventory_hash:
        raise ValueError('Diagnostic evidence uses a different inventory.')
    # Semantic capture uses raw bytes, syntax tools use LF-normalized hashes.
    # The capture manifest explicitly attests this cross-contract association.
    if manifest['sourceAttestation']['inventorySha256'] != inventory_hash:
        raise ValueError('Snapshot attestation uses a different inventory.')
    if manifest['sourceAttestation']['dependencyInputHash'] != reports['dependencies']['provenance']['inputHash']:
        raise ValueError('Snapshot attestation uses different semantic inputs.')
    if not manifest['sourceAttestation']['reviewer'].strip():
        raise ValueError('Missing snapshot producer/reviewer attestation.')
    return manifest, reports


def symbol_rows(inventory):
    rows = defaultdict(list)
    for row in inventory['Symbols']:
        rows[row['Key']].append(row)
    return {key: sorted(value, key=lambda r: (r['Path'], r['Line'])) for key, value in rows.items()}


def measures(reports):
    result = Counter()
    for row in reports['structural']['Metrics']:
        for name in ('Cyclomatic', 'Cognitive', 'MaxNesting', 'ExecutableLines', 'ParameterCount'):
            result[('structural.' + name, row['SymbolKey'])] += row[name]
    for candidate in reports['duplication']['Candidates']:
        for occurrence in candidate['Occurrences']:
            result[('duplication.occurrences', occurrence['Source']['SymbolKey'])] += 1
    for row in reports['diagnostics']['findings']:
        result[('diagnostics.' + row['rule'], row['symbol'])] += row['count']
    for row in reports['dependencies']['semantic']['symbols']:
        for flag in ('unwiredApiCandidate', 'deadCodeCandidate'):
            if row.get(flag):
                result[('dependencies.' + flag, row['key'])] = 1
    for edge in reports['dependencies'].get('conformance', {}).get('componentDependencies', []):
        if edge['classification'] == 'forbidden':
            result[('dependencies.forbidden-edge', 'component:' + edge['source'] + '->' + edge['target'])] += 1
    return result


def validate_exceptions(registry, as_of):
    if registry['schemaVersion'] != 1:
        raise ValueError('Unsupported exception registry schema.')
    seen = set()
    for entry in registry['entries']:
        for field in ('rule', 'symbol', 'issue', 'rationale', 'owner', 'reviewer', 'reviewCondition', 'expires'):
            if not isinstance(entry.get(field), str) or not entry[field].strip():
                raise ValueError('Exception missing field: ' + field)
        if '*' in entry['rule'] or '*' in entry['symbol']:
            raise ValueError('Wildcard exceptions are prohibited.')
        if not entry['issue'].startswith('https://github.com/marctjones/excise/issues/') or not entry['issue'].rsplit('/', 1)[-1].isdigit():
            raise ValueError('Exception requires an exact repository issue URL.')
        key = (entry['rule'], entry['symbol'])
        if key in seen:
            raise ValueError('Duplicate exception key.')
        seen.add(key)
        if not isinstance(entry.get('maximum'), int) or isinstance(entry['maximum'], bool) or entry['maximum'] < 0:
            raise ValueError('Exception requires a bounded nonnegative maximum.')
        date.fromisoformat(entry['expires'])
    return {(e['rule'], e['symbol']): e for e in registry['entries'] if date.fromisoformat(e['expires']) >= as_of}


def build_report(before, after, registry, as_of, provenance):
    for family in FAMILIES:
        old, new = before[family], after[family]
        for name in ('SchemaVersion', 'schemaVersion', 'ToolVersion', 'ToolSourceHash', 'RoslynVersion', 'ParseContract', 'AnalyzerVersion', 'DotnetSdkVersion', 'ScopeHash', 'MetricDefinitions', 'InputsAndExclusions', 'generator', 'sdkVersion', 'formatterVersion', 'ruleset'):
            if old.get(name) != new.get(name):
                raise ValueError(f'{family} {name} changed; recapture both snapshots with the reviewed tool contract.')
    for name in ('generator', 'roslynVersion', 'scope', 'toolAssemblyHash'):
        if before['dependencies']['semantic'].get(name) != after['dependencies']['semantic'].get(name):
            raise ValueError('Semantic contract changed: ' + name)
    old_rows, new_rows = symbol_rows(before['inventory']), symbol_rows(after['inventory'])
    changed = {k for k in old_rows.keys() | new_rows.keys() if old_rows.get(k) != new_rows.get(k)}
    semantic_old = {r['key']: r for r in before['dependencies']['semantic']['symbols']}
    semantic_new = {r['key']: r for r in after['dependencies']['semantic']['symbols']}
    # Bound semantics can change when a callee changes without declaration bytes changing.
    changed.update(k for k in semantic_old.keys() | semantic_new.keys() if semantic_old.get(k) != semantic_new.get(k))
    old_edges = {json.dumps(r, sort_keys=True) for r in before['dependencies']['semantic'].get('symbolEdges', [])}
    new_edges = {json.dumps(r, sort_keys=True) for r in after['dependencies']['semantic'].get('symbolEdges', [])}
    for edge in old_edges ^ new_edges:
        row = json.loads(edge); changed.update((row['source'], row['target']))
    old_values, new_values = measures(before), measures(after)
    for key in semantic_old.keys() | semantic_new.keys():
        old, new = semantic_old.get(key), semantic_new.get(key)
        if old and old.get('publicApi') and (not new or old.get('signature') != new.get('signature')):
            new_values[('dependencies.api-removal-or-signature-change', key)] = 1
    old_tests = {r['Key']: r for r in before['testStrength']['Symbols']}
    new_tests = {r['Key']: r for r in after['testStrength']['Symbols']}
    for key in old_tests.keys() & new_tests.keys():
        old, new = old_tests[key], new_tests[key]
        old_attribution = {r['Key'] for r in old['Tests']}
        new_attribution = {r['Key'] for r in new['Tests']}
        if old_attribution - new_attribution:
            new_values[('testStrength.direct-attribution-loss', key)] = len(old_attribution - new_attribution)
    changed.update(key for rule, key in old_values.keys() | new_values.keys() if old_values[(rule, key)] != new_values[(rule, key)])
    valid = validate_exceptions(registry, as_of)
    reviews = []
    for rule, key in sorted(old_values.keys() | new_values.keys()):
        if key not in changed:
            continue
        old, new = old_values[(rule, key)], new_values[(rule, key)]
        status = 'increased-review-required' if new > old else 'reduced' if new < old else 'existing-debt'
        exception = valid.get((rule, key))
        if new > old and exception and new <= exception['maximum']:
            status = 'explicit-exception'
        reviews.append({'rule': rule, 'symbol': key, 'before': old, 'after': new, 'status': status,
                        'exception': exception if status == 'explicit-exception' else None})
    test_rows = {r['Key']: r for r in after['testStrength']['Symbols']}
    rows = [{'symbol': k, 'status': 'added' if k not in old_rows and k not in semantic_old else 'removed' if k not in new_rows and k not in semantic_new else 'changed',
             'before': old_rows.get(k, []), 'after': new_rows.get(k, []),
             'dependenciesBefore': semantic_old.get(k), 'dependenciesAfter': semantic_new.get(k),
             'testStrength': test_rows.get(k, {'status': 'unknown-or-outside-attribution-scope'})} for k in sorted(changed)]
    return {'schemaVersion': 1, 'generator': 'code-health-review/1', 'asOf': as_of.isoformat(),
            'toolSourceSha256': digest(Path(__file__).read_bytes()), 'provenance': provenance,
            'changedSymbols': rows, 'reviews': reviews,
            'existingDebt': [{'rule': r, 'symbol': k, 'value': v} for (r, k), v in sorted(new_values.items()) if v],
            'expiredExceptions': [e for e in registry['entries'] if date.fromisoformat(e['expires']) < as_of],
            'dependencyEvidence': {name: after['dependencies'].get(name) for name in ('conformance', 'blindSpots', 'dynamicMechanisms')},
            'dependencyEdges': {stage: {name: report['dependencies']['semantic'].get(name, []) for name in ('symbolEdges', 'projectEdges')} for stage, report in (('before', before), ('after', after))},
            'testEvidenceLimits': {name: after['testStrength'].get(name) for name in ('UnmatchedInventorySymbols', 'UnresolvedTestInvocationCount', 'Execution', 'Coverage')},
            'diagnosticProvenance': 'inventory-fingerprinted',
            'policy': 'Individual measurement increases require bounded reviewed exceptions; unchanged debt does not block. No aggregate score. Metrics never authorize deletion, weaker behavioral tests or release claims.'}


def self_test():
    import tempfile
    key = 'Probe.csproj::M:Probe.A'
    inventory = {'Inputs': [], 'Symbols': [{'Key': key, 'Path': 'Probe.cs', 'Line': 1, 'DeclarationHash': 'old'}]}
    base = {'inventory': inventory, 'structural': {'Metrics': [{'SymbolKey': key, 'Cyclomatic': 1, 'Cognitive': 0, 'MaxNesting': 0, 'ExecutableLines': 1, 'ParameterCount': 0}]},
            'duplication': {'Candidates': []}, 'diagnostics': {'findings': []}, 'dependencies': {'semantic': {'symbols': []}}, 'testStrength': {'Symbols': []}}
    current = json.loads(json.dumps(base)); current['structural']['Metrics'][0]['Cyclomatic'] = 2
    registry = {'schemaVersion': 1, 'entries': []}; day = date(2026, 10, 4)
    report = build_report(base, current, registry, day, {})
    assert any(r['status'] == 'increased-review-required' for r in report['reviews'])
    entry = {'rule': 'structural.Cyclomatic', 'symbol': key, 'issue': 'https://github.com/marctjones/excise/issues/1945', 'rationale': 'Planted review', 'owner': 'maintainer', 'reviewer': 'reviewer', 'reviewCondition': 'Recheck next change', 'expires': '2026-10-05', 'maximum': 2}
    registry['entries'] = [entry]
    accepted = build_report(base, current, registry, day, {})
    assert any(r['status'] == 'explicit-exception' for r in accepted['reviews'])
    assert encoded(accepted) == encoded(build_report(base, current, registry, day, {}))
    assert any(r['status'] == 'increased-review-required' for r in build_report(base, current, registry, date(2026, 10, 6), {})['reviews'])
    current['structural']['Metrics'][0]['Cyclomatic'] = 3
    assert any(r['status'] == 'increased-review-required' for r in build_report(base, current, registry, day, {})['reviews'])
    entry['symbol'] = '*'
    try:
        validate_exceptions(registry, day)
        raise AssertionError('Wildcard accepted')
    except ValueError:
        pass
    assert not any(r['status'] == 'increased-review-required' for r in build_report(base, base, {'schemaVersion': 1, 'entries': []}, day, {})['reviews'])
    test_before = json.loads(json.dumps(base)); test_after = json.loads(json.dumps(base))
    test_before['testStrength']['Symbols'] = [{'Key': key, 'Tests': [{'Key': 'OldTest'}]}]
    test_after['testStrength']['Symbols'] = [{'Key': key, 'Tests': [{'Key': 'ReplacementTest'}]}]
    assert any(r['rule'] == 'testStrength.direct-attribution-loss' and r['after'] == 1
               for r in build_report(test_before, test_after, {'schemaVersion': 1, 'entries': []}, day, {})['reviews'])
    # Exercise the immutable artifact/input join independently of the measurement comparator.
    fixture = json.loads(json.dumps(base))
    fixture['inventory']['Inputs'] = [{'Path': 'Probe.cs', 'SourceHash': 'source'}]
    for family in FAMILIES:
        fixture[family]['SchemaVersion' if family != 'diagnostics' and family != 'dependencies' else 'schemaVersion'] = 1
    for family in ('structural', 'duplication', 'testStrength'):
        fixture[family]['Inputs'] = [{'Path': 'Probe.cs', 'Sha256': 'source'}]
    fixture['dependencies']['provenance'] = {'inputHash': 'semantic-input'}
    with tempfile.TemporaryDirectory(prefix='excise-review-selftest-') as directory:
        root = Path(directory)
        inventory_hash = digest(encoded(fixture['inventory']))
        fixture['testStrength']['InventorySha256'] = inventory_hash
        fixture['diagnostics']['inventorySha256'] = inventory_hash
        artifacts = {}
        for family, content in fixture.items():
            data = encoded(content); (root / (family + '.json')).write_bytes(data)
            artifacts[family] = {'path': family + '.json', 'sha256': digest(data)}
        manifest = {'schemaVersion': 1, 'artifacts': artifacts, 'sourceAttestation': {'inventorySha256': inventory_hash, 'dependencyInputHash': 'semantic-input', 'reviewer': 'fixture'}}
        path = root / 'snapshot.json'; path.write_bytes(encoded(manifest))
        assert load_bundle(path)[1] == fixture
        fixture['structural']['Inputs'][0]['Sha256'] = 'other-source'
        data = encoded(fixture['structural']); (root / 'structural.json').write_bytes(data)
        manifest['artifacts']['structural']['sha256'] = digest(data); path.write_bytes(encoded(manifest))
        try:
            load_bundle(path); raise AssertionError('Mixed snapshot accepted')
        except ValueError:
            pass
        (root / 'structural.json').write_bytes(b'{}')
        try:
            load_bundle(path); raise AssertionError('Tampered artifact accepted')
        except ValueError:
            pass
    print('PASS: deterministic report; measurement-induced changes; unchanged debt; bounded/expired/wildcard exceptions; mixed snapshot and artifact tampering rejected.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('report', 'self-test', 'bundle'))
    parser.add_argument('--before', type=Path); parser.add_argument('--after', type=Path)
    parser.add_argument('--exceptions', type=Path, default=Path('tests/code-health/exceptions.json'))
    parser.add_argument('--as-of', type=date.fromisoformat)
    parser.add_argument('--directory', type=Path)
    parser.add_argument('--reviewer')
    args = parser.parse_args()
    if args.mode == 'self-test':
        self_test(); return 0
    if args.mode == 'bundle':
        if not args.directory or not args.reviewer:
            parser.error('bundle requires --directory and --reviewer')
        artifacts = {f: {'path': f + '.json', 'sha256': digest((args.directory / (f + '.json')).read_bytes())} for f in FAMILIES}
        dependency = json.loads((args.directory / 'dependencies.json').read_bytes())
        manifest = {'schemaVersion': 1, 'artifacts': artifacts,
                    'sourceAttestation': {'inventorySha256': artifacts['inventory']['sha256'],
                                          'dependencyInputHash': dependency['provenance']['inputHash'], 'reviewer': args.reviewer}}
        # Validate the complete bundle before printing anything. Manifest creation never writes source.
        import tempfile
        with tempfile.NamedTemporaryFile(dir=args.directory, suffix='.json') as temp:
            temp.write(encoded(manifest)); temp.flush()
            load_bundle(Path(temp.name))
        sys.stdout.buffer.write(encoded(manifest)); return 0
    if not args.before or not args.after or not args.as_of:
        parser.error('report requires --before, --after and explicit --as-of date')
    bm, before = load_bundle(args.before); am, after = load_bundle(args.after)
    report = build_report(before, after, json.loads(args.exceptions.read_bytes()), args.as_of,
                          {'beforeManifestSha256': digest(args.before.read_bytes()), 'afterManifestSha256': digest(args.after.read_bytes()),
                           'before': bm, 'after': am, 'exceptionsSha256': digest(args.exceptions.read_bytes())})
    sys.stdout.buffer.write(encoded(report))
    return 1 if any(r['status'] == 'increased-review-required' for r in report['reviews']) else 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError) as error:
        print('ERROR: ' + str(error), file=sys.stderr); sys.exit(2)
