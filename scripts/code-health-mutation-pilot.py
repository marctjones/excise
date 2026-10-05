#!/usr/bin/env python3
"""Bounded, isolated Core mutation experiment; observations only. See #1943."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


SOURCE = "Excise.Core/Encryption/PdfPermissions.cs"
ACTION = "Excise.Core/Encryption/DocumentAction.cs"
TEST = "Excise.Core.Tests/Security/PdfPermissionsTests.cs"
MUTANTS = [
    ("extract-bit-5-to-6", "DocumentAction.Extract => Bit(5)", "DocumentAction.Extract => Bit(6)"),
    ("accessibility-bit-10-to-5", "DocumentAction.ExtractForAccessibility => Bit(10)", "DocumentAction.ExtractForAccessibility => Bit(5)"),
    ("fill-forms-or-to-and", "DocumentAction.FillForms => Bit(6) || Bit(9)", "DocumentAction.FillForms => Bit(6) && Bit(9)"),
    ("create-form-and-to-or", "DocumentAction.CreateFormField => Bit(6) && Bit(4)", "DocumentAction.CreateFormField => Bit(6) || Bit(4)"),
    ("high-quality-and-to-or", "DocumentAction.PrintHighQuality => Bit(3) && Bit(12)", "DocumentAction.PrintHighQuality => Bit(3) || Bit(12)"),
    ("bit-index-off-by-one", "1u << (oneBasedBit - 1)", "1u << oneBasedBit"),
]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def selection(test):
    marker = "    // ---- Exposure on PdfDocument"
    if test.count(marker) != 1:
        raise ValueError("Raw permission test slice marker missing or ambiguous")
    prefix = test.split(marker)[0]
    for directive in ("using Excise.Core.Document;\n", "using Excise.Core.Writing;\n", "using Excise.TestSupport;\n"):
        prefix = prefix.replace(directive, "")
    return prefix + "}\n"


def counts(trx):
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    doc = ET.parse(trx)
    counters = doc.find(".//t:Counters", ns)
    if counters is None:
        raise ValueError("Missing TRX counters")
    return {key: int(counters.attrib.get(key, 0)) for key in ("total", "executed", "passed", "failed", "notExecuted")}


def classify(code, counter, failures, expected_tests=None):
    if counter["executed"] == 0 or counter["notExecuted"] != 0:
        return "invalid-zero-or-skipped-tests"
    if counter["total"] != counter["executed"] or counter["passed"] + counter["failed"] != counter["executed"]:
        return "invalid-incomplete-results"
    if expected_tests is not None and counter["executed"] != expected_tests:
        return "invalid-test-population"
    if counter["failed"] > 0:
        # A host/load exception can also produce Failed test rows. Retain only
        # recognized assertion failures as kills for this deliberately narrow pilot.
        if code != 1 or len(failures) != counter["failed"] or not all(
            "Xunit.Sdk." in failure or "AwesomeAssertions." in failure for failure in failures
        ):
            return "invalid-nonassertion-failure"
        return "killed"
    return "survived" if code == 0 else "invalid-infrastructure"


def execute(directory, label, timeout, log_directory, expected_tests=None):
    trx = directory / "results" / (label + ".trx")
    cmd = ["dotnet", "test", "Pilot.csproj", "-v", "quiet", "--logger", "trx;LogFileName=" + trx.name,
           "--results-directory", "results", "-p:NuGetAudit=false"]
    start = time.monotonic()
    with tempfile.TemporaryFile() as stream:
        process = subprocess.Popen(cmd, cwd=directory, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        timed_out = False
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGKILL)
            code = process.wait()
        stream.seek(0)
        output = stream.read()
    (log_directory / (label + ".log")).write_bytes(output)
    row = {"Label": label, "RuntimeSeconds": round(time.monotonic() - start, 3), "ExitCode": code,
           "Command": " ".join(cmd), "LogSha256": sha(output)}
    if timed_out:
        row["Outcome"] = "timeout"
    elif not trx.exists():
        row["Outcome"] = "invalid-no-results"
    else:
        row["Counters"] = counts(trx)
        row["TrxSha256"] = sha(trx.read_bytes())
        (log_directory / trx.name).write_bytes(trx.read_bytes())
        counter = row["Counters"]
        ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        failures = ["\n".join(result.itertext()) for result in ET.parse(trx).findall(".//t:UnitTestResult", ns)
                    if result.attrib.get("outcome") == "Failed"]
        row["Outcome"] = classify(code, counter, failures, expected_tests)
    print(label + ": " + row["Outcome"] + " (" + str(row["RuntimeSeconds"]) + "s)", flush=True)
    return row


def run(root, output, repeats, timeout, log_directory):
    original = (root / SOURCE).read_text().replace("\r\n", "\n")
    tests = selection((root / TEST).read_text().replace("\r\n", "\n"))
    for _, before, _ in MUTANTS:
        if original.count(before) != 1:
            raise ValueError("Mutation precondition failed: " + before)
    source_paths = [SOURCE, ACTION, TEST, "Excise.Core.Tests/Excise.Core.Tests.csproj", "global.json"]
    inputs = [{"Path": path, "Sha256": sha((root / path).read_bytes())} for path in source_paths]
    packages = ET.parse(root / "Excise.Core.Tests/Excise.Core.Tests.csproj").findall(".//PackageReference")
    chosen = {name: next(p.attrib["Version"] for p in packages if p.attrib.get("Include") == name)
              for name in ("AwesomeAssertions", "Microsoft.NET.Test.Sdk", "xunit.v3", "xunit.runner.visualstudio")}
    project = ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
               '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject>'
               '</PropertyGroup><ItemGroup>' + ''.join('<PackageReference Include="' + name + '" Version="' + version + '" />'
                                                      for name, version in sorted(chosen.items())) + '</ItemGroup></Project>\n')
    log_directory.mkdir(parents=True, exist_ok=True)
    report = {"SchemaVersion": 1, "ToolVersion": "code-health-mutation-pilot/1", "ToolSourceHash": sha(Path(__file__).read_bytes()),
              "SourceRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
              "Inputs": inputs, "SelectedTestSourceSha256": sha(tests.encode()), "PilotProjectSha256": sha(project.encode()),
              "DotnetSdkVersion": subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip(),
              "PopulationContract": "Six explicitly listed first-order mutants, one replacement each, on PdfPermissions permission decoding. This is a bounded chosen population, not an exhaustive mutation inventory or repository mutation score.",
              "TestContract": "Unmodified raw-bitmask tests from PdfPermissionsTests up to its Exposure on PdfDocument marker; only three unused dependency usings removed. PdfPermissions.cs and DocumentAction.cs copied into isolated net10.0 test project. Existing integration/corpus tests are excluded explicitly. No production file is mutated in the checkout.",
              "DecisionContract": "No hard mutation gate or coverage threshold. Test assertion failures kill mutants; compile errors, missing/zero/skipped tests and other infrastructure failures are invalid, not killed. A timeout is reported separately. Baseline must pass without skips.",
              "Reproduce": "python3 scripts/code-health-mutation-pilot.py --root . --output OUT.json --logs-directory LOG_DIR --repeat " + str(repeats) + " --timeout " + str(timeout),
              "RepeatCount": repeats, "TimeoutSecondsPerRun": timeout, "MutantPopulation": len(MUTANTS), "Baselines": [], "Mutants": []}
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix="excise-mutation-pilot-") as temporary:
        directory = Path(temporary)
        (directory / "Pilot.csproj").write_text(project)
        (directory / "global.json").write_bytes((root / "global.json").read_bytes())
        (directory / "DocumentAction.cs").write_bytes((root / ACTION).read_bytes())
        (directory / "Tests.cs").write_text(tests)
        source_file = directory / "PdfPermissions.cs"
        for trial in range(1, repeats + 1):
            source_file.write_text(original)
            baseline = execute(directory, "baseline-" + str(trial), timeout, log_directory)
            report["Baselines"].append(baseline)
            if baseline["Outcome"] != "survived":
                raise RuntimeError("Baseline must pass with nonzero tests and no skips; inspect " + str(log_directory))
            for name, before, after in MUTANTS:
                source_file.write_text(original.replace(before, after, 1))
                row = execute(directory, name + "-" + str(trial), timeout, log_directory, baseline["Counters"]["executed"])
                report["Mutants"].append({"Id": name, "Trial": trial, "Original": before, "Replacement": after,
                                          "MutatedSourceSha256": sha(source_file.read_bytes()), **row})
    report["TotalRuntimeSeconds"] = round(time.monotonic() - started, 3)
    report["CountsPerTrial"] = [{"Trial": trial, **{outcome: sum(m["Outcome"] == outcome and m["Trial"] == trial for m in report["Mutants"])
                                for outcome in ("killed", "survived", "timeout")},
                               "invalid": sum(m["Outcome"].startswith("invalid") and m["Trial"] == trial for m in report["Mutants"])}
                              for trial in range(1, repeats + 1)]
    report["ReproducedOutcomes"] = all(len({m["Outcome"] for m in report["Mutants"] if m["Id"] == name}) == 1
                                      for name, _, _ in MUTANTS) if repeats > 1 else None
    # Hash inputs again so concurrent edits cannot silently change the pilot's provenance.
    if inputs != [{"Path": path, "Sha256": sha((root / path).read_bytes())} for path in source_paths]:
        raise RuntimeError("Pilot inputs changed during execution; rerun on stable inputs")
    output.write_text(json.dumps(report, indent=2) + "\n")
    if any(m["Outcome"].startswith("invalid") for m in report["Mutants"]):
        raise RuntimeError("Pilot includes invalid runs; report preserves them for review")


def self_test():
    assert len(MUTANTS) == len({m[0] for m in MUTANTS}) == 6
    fixture = "using Excise.Core.Document;\nclass Example {\n    // ---- Exposure on PdfDocument\nvoid Integration() {}\n}"
    assert "Integration" not in selection(fixture) and "Excise.Core.Document" not in selection(fixture)
    try:
        selection("no marker")
    except ValueError:
        pass
    else:
        raise AssertionError("Missing slice marker must fail")
    counter = {"total": 2, "executed": 2, "passed": 1, "failed": 1, "notExecuted": 0}
    assert classify(1, counter, ["Xunit.Sdk.XunitException"]) == "killed"
    assert classify(1, counter, ["System.IO.IOException"]).startswith("invalid")
    assert classify(1, counter, ["Xunit.Sdk.XunitException"], 3) == "invalid-test-population"
    assert classify(1, dict(counter, executed=0), []) == "invalid-zero-or-skipped-tests"
    assert classify(1, dict(counter, notExecuted=1), []) == "invalid-zero-or-skipped-tests"
    assert classify(0, dict(counter, passed=2, failed=0), []) == "survived"
    assert classify(2, counter, ["Xunit.Sdk.XunitException"]).startswith("invalid")
    print("PASS: bounded population and deterministic slice; missing marker, zero/skipped/drifted test population and infrastructure failures rejected; assertion kills separated from survivors")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--logs-directory", type=Path)
    parser.add_argument("--repeat", type=int, default=2, choices=range(1, 4))
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args()
    if options.self_test:
        self_test()
    else:
        if options.output is None or options.logs_directory is None or not 1 <= options.timeout <= 300:
            parser.error("--output and --logs-directory are required; timeout must be 1..300 seconds")
        run(options.root.resolve(), options.output, options.repeat, options.timeout, options.logs_directory)
