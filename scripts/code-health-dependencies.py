#!/usr/bin/env python3
"""Capture/compare authoritative dependency and bound source API evidence (#1942)."""
from __future__ import annotations

import argparse
from collections import defaultdict
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

import check_architecture_registry as architecture
import check_unwired_api as unwired

ROOT = Path(__file__).resolve().parents[1]
GENERATOR = "code-health-dependencies/2"
CAVEAT = ("Static unreachable/unwired symbols are review candidates, never deletion approval. "
          "Inspect seeds, XAML, reflection, DI, scripting, native entry points, external consumers "
          "and the architecture registry before changing behavior. Tests-only APIs are reported separately.")


def encoded(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")) + "\n").encode()


def digest(value):
    return hashlib.sha256(value).hexdigest()


def run(*args, capture=False):
    return subprocess.run(args, cwd=ROOT, check=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def git(*args):
    return run("git", *args, capture=True).decode().strip()


def inputs():
    # Include untracked workspace source because MSBuild's implicit Compile glob includes it.
    paths = run("git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", capture=True)
    candidates = set(paths.decode().split("\0")) - {""}
    suffixes = {".cs", ".csproj", ".axaml", ".xaml", ".resx", ".props", ".targets", ".sln", ".slnx", ".config", ".editorconfig"}
    explicit = {"global.json", "architecture/design.json", "architecture/assessment.json",
                "architecture/inventory.generated.json", "architecture/repository-scope.json",
                "scripts/code-health-dependencies.py", "scripts/check_architecture_registry.py",
                "scripts/check_unwired_api.py", "scripts/validate_json_schema.py", ".editorconfig"}
    relevant = set(path for path in candidates if
                      not set(Path(path).parts).intersection({"bin", "obj", ".claude", "test-pdfs", "third_party"})
                      and (Path(path).suffix.lower() in suffixes or path in explicit
                           or path.endswith(".approved.txt") or path.endswith("packages.lock.json")))
    # The authoritative lexical heuristic also consumes ignored/vendored source.
    # Fingerprint its own crawler scope rather than implying Git-only provenance.
    for directory, children, files in os.walk(ROOT):
        children[:] = [child for child in children if child not in unwired.SKIP_DIRS]
        for name in files:
            if name.endswith((".cs", ".axaml")) or (Path(directory).name == "PublicApi" and name.endswith(".approved.txt")):
                relevant.add((Path(directory) / name).relative_to(ROOT).as_posix())
    rows = []
    for path in sorted(relevant):
        source = ROOT / path
        rows.append({"path": path, "sha256": digest(source.read_bytes()) if source.exists() else None})
    return rows


def lexical_unwired():
    prod, test, occurrences, _ = unwired.source_index(str(ROOT))
    rows = []
    approvals = []
    for path in sorted(unwired.approved_files(str(ROOT))):
        assembly = unwired.snapshot_assembly(path)
        dead, tests_only = unwired.classify(sorted(unwired.identifiers(path, 8)), prod, test, occurrences)
        rows.extend({"assembly": assembly, "identifier": name, "state": state}
                    for state, names in (("nowhere", dead), ("tests-only", tests_only)) for name in names)
        approvals.append({"path": Path(path).relative_to(ROOT).as_posix(),
                          "sha256": digest(Path(path).read_bytes())})
    return {"generator": "scripts/check_unwired_api.py", "minimumIdentifierLength": 8,
            "contract": "Existing approved API name-only heuristic; not bound symbol reachability. Same-name identifiers may collide; use semantic rows for overload/type-specific review.",
            "approvedArtifacts": approvals,
            "candidates": sorted(rows, key=lambda row: (row["assembly"], row["identifier"], row["state"]))}


def capture(output):
    # Refresh workspace evaluation state before taking the stable-input snapshot.
    run("dotnet", "restore", "excise.sln", "-p:NuGetAudit=false", "--force-evaluate",
        "--disable-build-servers", "--verbosity", "quiet")
    before = inputs()
    revision = git("rev-parse", "HEAD")
    with tempfile.TemporaryDirectory(prefix="excise-dependency-evidence-") as temp:
        raw = Path(temp) / "semantic.json"
        # The reachability ratchet writes ONLY into the temporary directory.
        run("dotnet", "run", "--no-restore", "--project", "tools/Excise.Reachability", "--",
            "--quiet", "--review-evidence-output", str(raw), "--update", "--baseline", str(Path(temp) / "baseline.tsv"))
        semantic = json.loads(raw.read_text())
    lexical = lexical_unwired()
    if before != inputs() or revision != git("rev-parse", "HEAD"):
        raise RuntimeError("Inputs changed during semantic analysis; retry after concurrent edits finish. No output published.")
    topology = semantic.pop("topology")
    conformance = architecture.generate_architecture_conformance(
        json.loads((ROOT / "architecture/design.json").read_text()),
        json.loads((ROOT / "architecture/inventory.generated.json").read_text()),
        json.loads((ROOT / "architecture/assessment.json").read_text()), topology)
    tracked = set(run("git", "ls-files", "-z", capture=True).decode().split("\0"))
    git_visible = set(run("git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", capture=True).decode().split("\0"))
    evidence = {"schemaVersion": 1, "generator": GENERATOR, "caveat": CAVEAT,
                "reproduce": "python3 scripts/code-health-dependencies.py capture OUTPUT.json",
                "provenance": {"sourceRevision": revision,
                               "treeState": "dirty" if git("status", "--porcelain", "--untracked-files=normal") else "clean",
                               "inputContract": "Raw bytes of Git-tracked and nonignored untracked C#/project/XAML/MSBuild/config/solution, approved APIs and named registry/generator inputs, plus every C#/AXAML/approved-API file in check_unwired_api's own crawler scope (including ignored or vendored files). Deleted tracked inputs are explicit null hashes. Semantic/config scan excludes bin/obj/corpus/nested worktrees/vendored roots; lexical scope uses the authoritative heuristic's SKIP_DIRS. Compiler/MSBuild evaluated platform is the current host.",
                               "inputHash": digest(encoded(before)), "inputs": before,
                               "untrackedInputs": sorted(row["path"] for row in before if row["path"] not in tracked),
                               "nonGitHeuristicInputs": sorted(row["path"] for row in before if row["path"] not in git_visible),
                               "sdkVersion": run("dotnet", "--version", capture=True).decode().strip(),
                               "freshness": "Regenerated semantic graph from current inputs; inputs and HEAD unchanged across capture. sourceRevision alone is not a freshness key."},
                "semantic": semantic,
                "projects": topology["projects"],
                "typeDependencies": topology["typeDependencies"],
                "dynamicMechanisms": topology["seeds"]["dynamicMechanisms"],
                "blindSpots": topology["blindSpots"], "conformance": conformance,
                "approvedApiHeuristic": lexical}
    evidence["boundaryReviewCandidates"] = boundary_review(evidence)
    if before != inputs() or revision != git("rev-parse", "HEAD"):
        raise RuntimeError("Inputs changed while deriving review views; no output published.")
    output.write_bytes(encoded(evidence))
    print(f"Captured {len(semantic['symbols'])} bound symbols to {output}; {evidence['provenance']['treeState']} input snapshot.")


def indexed(rows, keys):
    result = {}
    for row in rows:
        key = tuple(row[name] for name in keys)
        if key in result:
            raise ValueError(f"Ambiguous evidence key {key!r}; refusing a lossy delta.")
        result[key] = row
    return result


def delta(before, after, keys):
    old, new = indexed(before, keys), indexed(after, keys)
    ordered = lambda values: sorted(values, key=lambda key: tuple("" if part is None else part for part in key))
    return {"added": [new[key] for key in ordered(new.keys() - old.keys())],
            "removed": [old[key] for key in ordered(old.keys() - new.keys())],
            "changed": [{"before": old[key], "after": new[key]} for key in ordered(old.keys() & new.keys()) if old[key] != new[key]]}


def validate_snapshot(snapshot):
    if snapshot.get("schemaVersion") != 1 or snapshot.get("generator") != GENERATOR:
        raise ValueError("Unsupported dependency capture contract; regenerate with this tool.")
    symbols = indexed(snapshot["semantic"]["symbols"], ("key",))
    indexed(snapshot["semantic"]["symbolEdges"], ("source", "target", "kind"))
    indexed(snapshot["semantic"]["projectEdges"], ("source", "target", "kind"))
    static_incoming = defaultdict(int)
    for edge in snapshot["semantic"]["symbolEdges"]:
        if (edge["source"],) not in symbols or (edge["target"],) not in symbols:
            raise ValueError("Dangling semantic edge; refusing an incomplete graph.")
        if edge["kind"] not in {"static-reference", "runtime-interface-dispatch"}:
            raise ValueError(f"Unknown symbol edge kind: {edge['kind']}")
        if edge["kind"] == "static-reference" and edge["source"] != edge["target"]:
            static_incoming[edge["target"]] += 1
    for row in snapshot["semantic"]["symbols"]:
        fan_in = static_incoming[row["key"]]
        if row["staticFanIn"] != fan_in or row["unwiredApiCandidate"] != (row["publicApi"] and fan_in == 0):
            raise ValueError(f"Inconsistent static fan-in/unwired flag: {row['key']}")
        if row["deadCodeCandidate"] == row["reachable"] or (row["seeds"] and not row["reachable"]):
            raise ValueError(f"Inconsistent seed/reachability flags: {row['key']}")


def boundary_review(snapshot):
    """Registry observations requiring review, not a new architecture classifier."""
    conformance = snapshot["conformance"]
    return {"contract": "Existing registry classifications identify review candidates. These observations are not automatic architectural verdicts, refactor instructions or removal proof.",
            "componentEdges": sorted((row for row in conformance["componentDependencies"]
                                      if row["classification"] in {"forbidden", "undeclared"}),
                                     key=lambda row: (row["source"], row["target"])),
            "unregisteredSymbols": sorted((row["key"] for row in snapshot["semantic"]["symbols"]
                                          if row["component"] is None)),
            "unownedCode": conformance.get("unownedCode", {"projects": []}),
            "unresolvedTypeDependencyCount": conformance.get("summary", {}).get("unresolvedTypeDependencies", 0),
            "unresolvedTypeDependencyExamples": conformance.get("unresolvedTypeDependencyExamples", []),
            "registryMismatches": conformance.get("registryMismatches", [])}


def entrypoints(snapshot):
    validate_snapshot(snapshot)
    incoming, outgoing = defaultdict(list), defaultdict(list)
    for edge in snapshot["semantic"]["symbolEdges"]:
        incoming[edge["target"]].append({"key": edge["source"], "kind": edge["kind"]})
        outgoing[edge["source"]].append({"key": edge["target"], "kind": edge["kind"]})
    rows = []
    for row in sorted(snapshot["semantic"]["symbols"], key=lambda row: row["key"]):
        roles = []
        if row["seeds"]:
            roles.append("conservative-seed")
        if row["publicApi"]:
            roles.append("public-api-surface")
        if row["unwiredApiCandidate"]:
            roles.append("unwired-public-api-review")
        if row["deadCodeCandidate"]:
            roles.append("static-unreachable-review")
        if row["component"] is None:
            roles.append("unregistered-component-review")
        if not roles:
            continue
        callers = sorted(incoming[row["key"]], key=lambda edge: (edge["key"], edge["kind"]))
        callees = sorted(outgoing[row["key"]], key=lambda edge: (edge["key"], edge["kind"]))
        rows.append({name: row[name] for name in ("key", "project", "classification", "kind", "component", "signature", "spans", "publicApi", "reachable", "staticFanIn", "unwiredApiCandidate", "deadCodeCandidate")} |
                    {"roles": roles, "seedRationale": sorted(row["seeds"], key=lambda seed: (seed["category"], seed["reason"])),
                     "callers": callers, "callees": callees, "testProjects": sorted(row["testProjects"]),
                     "testReferenceState": "tests-only" if row["unwiredApiCandidate"] and row["testProjects"] else
                         "no-observed-static-or-test-references" if row["unwiredApiCandidate"] else "not-unwired-public-api"})
    return {"schemaVersion": 1, "generator": GENERATOR, "caveat": CAVEAT,
            "sourceSnapshotSha256": digest(encoded(snapshot)), "provenance": snapshot["provenance"],
            "inventoryContract": "Seeded symbols are conservative reachability roots, not observed runtime execution. Public APIs and unreachable/unregistered symbols are entrypoint or removal review candidates. Callers/callees are direct bound production graph references, not exclusively invocations; runtime-interface-dispatch is separately labeled. Static fan-in excludes self-references and runtime dispatch. Test-project references may be indirect. No dynamic caller is invented.",
            "summary": {"inventoryRows": len(rows), "seededSymbols": sum(bool(row["seedRationale"]) for row in rows),
                        "unwiredApiCandidates": sum(row["unwiredApiCandidate"] for row in rows),
                        "staticUnreachableCandidates": sum(row["deadCodeCandidate"] for row in rows)},
            "entrypoints": rows, "boundaryReviewCandidates": boundary_review(snapshot),
            "dynamicMechanisms": snapshot["dynamicMechanisms"], "blindSpots": snapshot["blindSpots"]}


def directed_graph(snapshot, level):
    validate_snapshot(snapshot)
    # JSON string quoting supplies DOT escaping without interpolating source text as syntax.
    quote = lambda value: json.dumps(str(value), ensure_ascii=False)
    nodes, edges = {}, []
    if level == "symbol":
        for row in snapshot["semantic"]["symbols"]:
            flags = ["reachable" if row["reachable"] else "static-unreachable review"]
            if row["unwiredApiCandidate"]:
                flags.append("unwired API review")
            if row["seeds"]:
                flags.append("seed: " + ", ".join(sorted({seed["category"] for seed in row["seeds"]})))
            nodes[row["key"]] = {"label": row["key"] + "\ncomponent: " + (row["component"] or "unregistered") + "\n" + "; ".join(flags),
                                 "shape": "doubleoctagon" if row["seeds"] else "box"}
        edges = [(edge["source"], edge["target"], {"label": edge["kind"],
                  "style": "dashed" if edge["kind"] == "runtime-interface-dispatch" else "solid"})
                 for edge in snapshot["semantic"]["symbolEdges"]]
    elif level == "project":
        projects = {row["path"]: row for row in snapshot.get("projects", [])}
        paths = set(projects) | {row["project"] for row in snapshot["semantic"]["symbols"]}
        paths.update(edge[name] for edge in snapshot["semantic"]["projectEdges"] for name in ("source", "target"))
        for path in sorted(paths):
            project = projects.get(path)
            context = (project["classification"] + "; component: " + (project["component"] or "unregistered")) if project else "outside analyzed symbol scope"
            nodes[path] = {"label": path + "\n" + context, "shape": "box"}
        edges = [(edge["source"], edge["target"], {"label": edge["kind"]})
                 for edge in snapshot["semantic"]["projectEdges"]]
    elif level == "component":
        components = {row["component"] for row in snapshot["semantic"]["symbols"] if row["component"] is not None}
        components.update(row["component"] for row in snapshot.get("projects", []) if row["component"] is not None)
        dependencies = snapshot["conformance"]["componentDependencies"]
        components.update(edge[name] for edge in dependencies for name in ("source", "target"))
        nodes = {component: {"label": component, "shape": "box"} for component in components}
        for edge in dependencies:
            classification = edge["classification"]
            attrs = {"label": f"{classification}; {edge['references']} references"}
            if classification in {"forbidden", "undeclared"}:
                attrs |= {"color": "red" if classification == "forbidden" else "orange", "style": "dashed"}
                attrs["label"] += "; review candidate"
            edges.append((edge["source"], edge["target"], attrs))
    else:
        raise ValueError(f"Unknown graph level: {level}")
    attrs = lambda values: ", ".join(f"{key}={quote(value)}" for key, value in sorted(values.items()))
    lines = ["digraph dependencies {", "  graph [" + attrs({"rankdir": "LR", "label":
             f"{level} dependencies: source -> referenced target\n" + CAVEAT,
             "tooltip": "snapshot SHA-256: " + digest(encoded(snapshot))}) + "];"]
    lines.extend(f"  {quote(key)} [{attrs(values)}];" for key, values in sorted(nodes.items()))
    lines.extend(f"  {quote(source)} -> {quote(target)} [{attrs(values)}];"
                 for source, target, values in sorted(edges, key=lambda edge: (edge[0], edge[1], encoded(edge[2]))))
    return "\n".join(lines) + "\n}\n"


def compare(before, after):
    if before.get("schemaVersion") != 1 or after.get("schemaVersion") != 1:
        raise ValueError("Unsupported dependency snapshot schema.")
    if before.get("generator") != after.get("generator"):
        raise ValueError("Different capture contracts; regenerate both snapshots with the same tool.")
    old, new = before["semantic"], after["semantic"]
    for name in ("generator", "roslynVersion", "scope"):
        if old.get(name) != new.get(name):
            raise ValueError(f"Semantic {name} differs; evidence contracts are not comparable.")
    if old.get("toolAssemblyHash") != new.get("toolAssemblyHash"):
        raise ValueError("Semantic generator assembly changed; regenerate both snapshots before interpreting deltas.")
    symbols = delta(old["symbols"], new["symbols"], ("key",))
    api = lambda rows: [{"key": row["key"], "signature": row["signature"], "project": row["project"], "spans": row["spans"]} for row in rows if row["publicApi"]]
    # Location-only changes remain symbol changes, but are not API signature changes.
    api_delta = delta(api(old["symbols"]), api(new["symbols"]), ("key",))
    api_delta["changed"] = [row for row in api_delta["changed"] if row["before"]["signature"] != row["after"]["signature"]]
    candidates = lambda rows, flag: [row for row in rows if row[flag]]
    edges = delta(old["symbolEdges"], new["symbolEdges"], ("source", "target", "kind"))
    touched = {row["key"] for row in symbols["added"] + symbols["removed"]}
    touched.update(row["after"]["key"] for row in symbols["changed"])
    touched.update(row[name] for row in edges["added"] + edges["removed"] for name in ("source", "target"))
    return {"schemaVersion": 1, "generator": GENERATOR, "caveat": CAVEAT,
            "reproduce": "python3 scripts/code-health-dependencies.py compare BEFORE.json AFTER.json OUTPUT.json",
            "beforeProvenance": before["provenance"], "afterProvenance": after["provenance"],
            "renameContract": "Renames are removed+added; documentation IDs retain overload/type identity. Signature changes with stable IDs are changed. No inferred identity.",
            "changedSymbolKeys": sorted(touched), "symbols": symbols, "api": api_delta, "symbolDependencies": edges,
            "projectDependencies": delta(old["projectEdges"], new["projectEdges"], ("source", "target", "kind")),
            "typeDependencies": delta(before["typeDependencies"], after["typeDependencies"], ("source", "sourceComponent", "target")),
            "componentDependencies": delta(before["conformance"]["componentDependencies"], after["conformance"]["componentDependencies"], ("source", "target")),
            "currentForbiddenEdges": [row for row in after["conformance"]["componentDependencies"] if row["classification"] == "forbidden"],
            "unwiredApiCandidates": delta(candidates(old["symbols"], "unwiredApiCandidate"), candidates(new["symbols"], "unwiredApiCandidate"), ("key",)),
            "deadCodeCandidates": delta(candidates(old["symbols"], "deadCodeCandidate"), candidates(new["symbols"], "deadCodeCandidate"), ("key",)),
            "approvedApiHeuristic": delta(before["approvedApiHeuristic"]["candidates"], after["approvedApiHeuristic"]["candidates"], ("assembly", "identifier", "state")),
            "approvedArtifacts": delta(before["approvedApiHeuristic"]["approvedArtifacts"], after["approvedApiHeuristic"]["approvedArtifacts"], ("path",)),
            "evaluation": delta(old.get("evaluation", []), new.get("evaluation", []), ("project",)),
            "entrypointSeeds": delta([row for row in old["symbols"] if row["seeds"]], [row for row in new["symbols"] if row["seeds"]], ("key",)),
            "boundaryReviewCandidates": boundary_review(after),
            "dynamicMechanisms": after["dynamicMechanisms"], "blindSpots": after["blindSpots"]}


def self_test():
    row = {"key": "Core.csproj::M:Fixture.Surface.Unwired", "project": "Core.csproj", "signature": "public void Fixture.Surface.Unwired()",
           "spans": [{"path": "Core.cs", "line": 1, "endLine": 1}], "publicApi": True,
           "classification": "shipping", "kind": "Method", "component": "core", "reachable": True,
           "seeds": [{"category": "public-api", "reason": "exported library surface"}],
           "unwiredApiCandidate": True, "deadCodeCandidate": False, "staticFanIn": 0, "testProjects": ["Core.Tests"]}
    empty = {"schemaVersion": 1, "generator": GENERATOR, "provenance": {"inputHash": "fixture", "treeState": "dirty"},
             "semantic": {"generator": "fixture", "roslynVersion": "fixture", "scope": "fixture", "symbols": [], "symbolEdges": [], "projectEdges": []},
             "typeDependencies": [], "conformance": {"componentDependencies": []}, "dynamicMechanisms": [], "blindSpots": [CAVEAT],
             "approvedApiHeuristic": {"candidates": [], "approvedArtifacts": []}}
    after = copy.deepcopy(empty)
    after["semantic"]["symbols"] = [row]
    design = {"designVersion": 1, "components": [{"id": "core", "dependsOn": []}, {"id": "app", "dependsOn": ["core"]}]}
    topology = {"sourceRevision": "fixture", "schemaVersion": 5, "projects": [
        {"name": name, "path": name + ".csproj", "classification": "shipping", "component": name} for name in ("core", "app")],
        "symbols": [{"kind": "type", "containingType": name + ".Type", "component": name, "project": name} for name in ("core", "app")],
        "typeDependencies": [{"source": "core.Type", "sourceComponent": "core", "target": "app.Type", "references": 1}], "blindSpots": [CAVEAT]}
    inventory = {"projects": [{"path": name + ".csproj", "classification": "shipping"} for name in ("core", "app")]}
    assessment = {"relationships": [{"source": "core", "target": "app", "type": "must-not-depend-on", "implementationStatus": "planned"}]}
    after["conformance"] = architecture.generate_architecture_conformance(design, inventory, assessment, topology)
    report = compare(empty, after)
    assert report["api"]["added"][0]["key"] == row["key"]
    assert report["unwiredApiCandidates"]["added"][0]["testProjects"] == ["Core.Tests"]
    assert report["currentForbiddenEdges"][0]["classification"] == "forbidden"
    assert CAVEAT in report["caveat"]
    moved = copy.deepcopy(after)
    moved["semantic"]["symbols"][0]["spans"][0]["line"] = 2
    assert compare(after, moved)["api"]["changed"] == []
    moved["semantic"]["symbols"][0]["signature"] += " changed"
    assert len(compare(after, moved)["api"]["changed"]) == 1
    moved["semantic"]["symbols"][0]["key"] += "Renamed"
    assert len(compare(after, moved)["api"]["added"]) == len(compare(after, moved)["api"]["removed"]) == 1
    assert encoded(compare(empty, after)) == encoded(compare(empty, copy.deepcopy(after)))
    assert not any(compare(after, after)[name][part] for name in ("api", "symbols", "typeDependencies", "projectDependencies", "symbolDependencies") for part in ("added", "removed", "changed"))
    fixture = copy.deepcopy(after)
    helper = copy.deepcopy(row) | {"key": 'App.csproj::M:Fixture.Quote"Slash\\Helper', "project": "App.csproj",
                                   "component": "app", "publicApi": False, "staticFanIn": 1, "seeds": [],
                                   "unwiredApiCandidate": False, "deadCodeCandidate": False, "reachable": True,
                                   "testProjects": []}
    fixture["semantic"]["symbols"].append(helper)
    orphan = helper | {"key": "App.csproj::M:Fixture.Stranded", "staticFanIn": 0,
                       "deadCodeCandidate": True, "reachable": False}
    fixture["semantic"]["symbols"].append(orphan)
    fixture["semantic"]["symbolEdges"] = [{"source": row["key"], "target": helper["key"], "kind": "static-reference"},
                                          {"source": helper["key"], "target": row["key"], "kind": "runtime-interface-dispatch"}]
    fixture["semantic"]["projectEdges"] = [{"source": "App.csproj", "target": "Core.csproj", "kind": "project-reference"}]
    fixture["projects"] = [{"path": "Core.csproj", "classification": "shipping", "component": "core"},
                           {"path": "App.csproj", "classification": "shipping", "component": "app"}]
    inventory_report = entrypoints(fixture)
    seeded = next(item for item in inventory_report["entrypoints"] if item["key"] == row["key"])
    assert seeded["seedRationale"] == row["seeds"]
    assert seeded["reachable"] and seeded["unwiredApiCandidate"] and seeded["testReferenceState"] == "tests-only"
    assert seeded["callers"] == [{"key": helper["key"], "kind": "runtime-interface-dispatch"}]
    assert seeded["callees"] == [{"key": helper["key"], "kind": "static-reference"}]
    assert inventory_report["boundaryReviewCandidates"]["componentEdges"][0]["classification"] == "forbidden"
    assert inventory_report["summary"]["staticUnreachableCandidates"] == 1
    reordered = copy.deepcopy(fixture)
    for name in ("symbols", "symbolEdges", "projectEdges"):
        reordered["semantic"][name].reverse()
    reordered["projects"].reverse()
    # View body ordering is deterministic; snapshot hashes identify the original exact snapshot.
    inventory_reordered = entrypoints(reordered)
    assert inventory_report["entrypoints"] == inventory_reordered["entrypoints"]
    for level in ("symbol", "project", "component"):
        graph = directed_graph(fixture, level)
        assert graph == directed_graph(copy.deepcopy(fixture), level)
        assert graph.replace(digest(encoded(fixture)), "HASH") == directed_graph(reordered, level).replace(digest(encoded(reordered)), "HASH")
        assert CAVEAT in graph and "source -> referenced target" in graph
    assert '"App.csproj" -> "Core.csproj"' in directed_graph(fixture, "project")
    assert '"core" -> "app"' in directed_graph(fixture, "component")
    assert "forbidden; 1 references; review candidate" in directed_graph(fixture, "component")
    assert json.dumps(helper["key"], ensure_ascii=False) in directed_graph(fixture, "symbol")
    assert orphan["key"] in directed_graph(fixture, "symbol")
    broken = copy.deepcopy(fixture)
    broken["semantic"]["symbolEdges"][0]["target"] = "missing-symbol"
    try:
        entrypoints(broken)
        raise AssertionError("Dangling semantic edge accepted")
    except ValueError:
        pass
    inconsistent = copy.deepcopy(fixture)
    inconsistent["semantic"]["symbols"][0]["staticFanIn"] = 1
    try:
        directed_graph(inconsistent, "symbol")
        raise AssertionError("Inconsistent fan-in accepted")
    except ValueError:
        pass
    print("PASS: planted forbidden edge and seeded unwired tests-only API; signature/rename/location deltas; direct callers and runtime dispatch; three deterministic directed DOT views, escaping, entrypoint rationale, dangling-edge rejection and candidate-only caveats.")
    return fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("self-test")
    cap = sub.add_parser("capture")
    cap.add_argument("output", type=Path)
    diff = sub.add_parser("compare")
    for name in ("before", "after", "output"):
        diff.add_argument(name, type=Path)
    graph = sub.add_parser("graph", help="Write a deterministic directed reviewer graph from a captured snapshot")
    graph.add_argument("snapshot", type=Path)
    graph.add_argument("output", type=Path)
    graph.add_argument("--level", choices=("symbol", "project", "component"), default="component")
    inventory = sub.add_parser("entrypoints", help="Write seed rationale, caller/callee and entrypoint review inventory")
    inventory.add_argument("snapshot", type=Path)
    inventory.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.command == "self-test":
        self_test()
    elif args.command == "capture":
        capture(args.output)
    elif args.command == "graph":
        args.output.write_text(directed_graph(json.loads(args.snapshot.read_text()), args.level), encoding="utf-8")
    elif args.command == "entrypoints":
        args.output.write_bytes(encoded(entrypoints(json.loads(args.snapshot.read_text()))))
    else:
        args.output.write_bytes(encoded(compare(json.loads(args.before.read_text()), json.loads(args.after.read_text()))))


if __name__ == "__main__":
    main()
