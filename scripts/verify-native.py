"""Verify real compiler-produced DLLs against LLVM and validate report schemas."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import shutil
import subprocess
import urllib.request

import jsonschema

ROOT = Path(__file__).resolve().parents[1]
SARIF_SCHEMA = (
    "https://raw.githubusercontent.com/SchemaStore/schemastore/"
    "2aded6096789c43a722556cf5019464ee747e495/src/schemas/json/sarif-2.1.0.json"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def llvm_surface(path: Path) -> tuple[set, set]:
    reader = shutil.which("llvm-readobj")
    if not reader:
        raise SystemExit("Install LLVM and put llvm-readobj on PATH.")
    text = subprocess.run(
        [reader, "--coff-exports", "--coff-imports", str(path)],
        check=True, capture_output=True, text=True,
    ).stdout
    exports = set()
    for block in re.findall(r"^Export \{\r?\n(.*?)^\}", text, re.M | re.S):
        ordinal = int(re.search(r"Ordinal: (\d+)", block).group(1))
        name = re.search(r"Name: ([^\r\n]*)", block).group(1)
        forwarded = re.search(r"ForwardedTo: ([^\r\n]+)", block)
        address = re.search(r"RVA: (0x[0-9A-Fa-f]+)", block)
        require(forwarded is not None or address is not None, f"Unrecognized LLVM export: {block}")
        if forwarded or int(address.group(1), 16):
            exports.add((ordinal, name or None, forwarded.group(1) if forwarded else None))
    imports = set()
    for kind, block in re.findall(r"^(Import|DelayImport) \{\r?\n(.*?)^\}", text, re.M | re.S):
        module = re.search(r"Name: ([^\r\n]+)", block).group(1).lower()
        for name, ordinal in re.findall(r"Symbol: (.*?) \((\d+)\)", block):
            imports.add((module, kind == "DelayImport", name or None, None if name else int(ordinal)))
    return exports, imports


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["x86", "x64", "arm64", "all"], default="all")
    parser.add_argument("--tool", type=Path, help="Installed tool executable; otherwise use the Release build.")
    args = parser.parse_args()
    command = [str(args.tool.resolve())] if args.tool else [
        "dotnet", str(ROOT / "src" / "SdkReleaseGuard.Cli" / "bin" / "Release" / "net10.0" / "sdk-release-guard.dll")
    ]
    report_schema = json.loads((ROOT / "docs" / "report-schema.json").read_text(encoding="utf-8"))
    report_validator = jsonschema.validators.validator_for(report_schema)(report_schema)
    schema_file = ROOT / "artifacts" / "schemas" / "sarif-2.1.0.json"
    if not schema_file.exists():
        schema_file.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(SARIF_SCHEMA, timeout=30) as response:
            schema_file.write_bytes(response.read())
    sarif_schema = json.loads(schema_file.read_text(encoding="utf-8"))
    sarif_validator = jsonschema.validators.validator_for(sarif_schema)(sarif_schema)
    architectures = ["x86", "x64", "arm64"] if args.arch == "all" else [args.arch]
    checks = 0

    def compare(before: Path, after: Path, exit_code: int, expected: dict, threshold="breaking", fmt="json"):
        nonlocal checks
        process = subprocess.run(
            [*command, "compare", "-b", str(before), "-c", str(after),
             "--format", fmt, "--fail-on", threshold],
            capture_output=True, text=True, encoding="utf-8",
        )
        require(process.returncode == exit_code,
                f"{before.parent.name} -> {after.parent.name}: expected exit {exit_code}, got {process.returncode}: {process.stderr}")
        require(not process.stderr, f"Unexpected diagnostics: {process.stderr}")
        document = json.loads(process.stdout)
        (sarif_validator if fmt == "sarif" else report_validator).validate(document)
        findings = document["runs"][0]["results"] if fmt == "sarif" else document["findings"]
        require(Counter(finding["ruleId"] for finding in findings) == Counter(expected),
                f"Unexpected findings for {before.parent.name} -> {after.parent.name}: {findings}")
        checks += 1
        return document

    for architecture in architectures:
        folder = ROOT / "artifacts" / "fixtures" / architecture
        base = folder / "baseline" / "sdk.dll"
        for name in ["baseline", "compatible", "breaking", "renumbered", "forward-old", "forward-new",
                     "eager", "delay", "import-added"]:
            dll = folder / name / "sdk.dll"
            require(dll.exists(), f"Missing {dll}; run scripts/build-fixtures.py --arch all first.")
            report = compare(dll, dll, 0, {})
            snapshot = report["candidate"]
            require(snapshot["architecture"] == architecture, "Wrong architecture")
            llvm_exports, llvm_imports = llvm_surface(dll)
            actual_exports = {
                (export["ordinal"], name, export["forwarder"])
                for export in snapshot["exports"]
                for name in (export["names"] or [None])
            }
            actual_imports = {
                (module["name"], module["delayLoaded"], symbol["name"], symbol["ordinal"])
                for module in snapshot["imports"] for symbol in module["symbols"]
            }
            require(actual_exports == llvm_exports, f"Export disagreement with LLVM: {dll}")
            require(actual_imports == llvm_imports, f"Import disagreement with LLVM: {dll}")
        compare(base, folder / "compatible" / "sdk.dll", 0, {"SRG105": 1, "SRG106": 1})
        breaking = {"SRG101": 1, "SRG103": 1, "SRG105": 1, "SRG106": 1}
        compare(base, folder / "breaking" / "sdk.dll", 1, breaking)
        compare(base, folder / "breaking" / "sdk.dll", 0, breaking, threshold="none")
        compare(base, folder / "breaking" / "sdk.dll", 1, breaking, fmt="sarif")
        compare(base, folder / "renumbered" / "sdk.dll", 1, {"SRG102": 2})
        compare(folder / "forward-old" / "sdk.dll", folder / "forward-new" / "sdk.dll", 0, {"SRG104": 1})
        compare(folder / "forward-old" / "sdk.dll", folder / "forward-new" / "sdk.dll", 1,
                {"SRG104": 1}, threshold="warning", fmt="sarif")
        compare(base, folder / "eager" / "sdk.dll", 0, {"SRG201": 1})
        compare(folder / "eager" / "sdk.dll", folder / "import-added" / "sdk.dll", 0, {"SRG202": 1})
        compare(folder / "delay" / "sdk.dll", folder / "eager" / "sdk.dll", 1,
                {"SRG203": 1, "SRG204": 1}, threshold="warning")
        compare(base, base, 0, {}, fmt="sarif")

    if args.arch == "all":
        root = ROOT / "artifacts" / "fixtures"
        compare(root / "x64" / "baseline" / "sdk.dll", root / "x86" / "baseline" / "sdk.dll",
                1, {"SRG001": 1, "SRG002": 1})
        compare(root / "x64" / "baseline" / "sdk.dll", root / "arm64" / "baseline" / "sdk.dll",
                1, {"SRG001": 1})

    invalid = ROOT / "artifacts" / "not-a-dll.txt"
    invalid.write_text("This is not a PE image.\n", encoding="utf-8")
    process = subprocess.run(
        [*command, "compare", "-b", str(invalid), "-c", str(invalid), "--fail-on", "none"],
        capture_output=True, text=True,
    )
    require(process.returncode == 2 and not process.stdout and process.stderr,
            "Invalid input must fail explicitly even in advisory mode.")
    checks += 1
    print(f"PASS: {checks} CLI scenarios; LLVM export/import agreement for {len(architectures) * 9} DLLs; JSON and SARIF schema validation.")


if __name__ == "__main__":
    main()
