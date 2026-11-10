"""Build tiny native PE fixtures with LLVM; never execute the generated DLLs."""

import argparse
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
TARGETS = {
    "x86": "i686-pc-windows-msvc",
    "x64": "x86_64-pc-windows-msvc",
    "arm64": "aarch64-pc-windows-msvc",
}


def build(architecture: str) -> Path:
    clang = shutil.which("clang")
    linker = shutil.which("lld-link")
    if not clang or not linker:
        raise SystemExit("Install LLVM and put clang and lld-link on PATH. No Windows SDK is required.")
    destination = ROOT / "artifacts" / "fixtures" / architecture
    destination.mkdir(parents=True, exist_ok=True)

    def compile_source(source: str, output: Path, *definitions: str) -> None:
        subprocess.run(
            [clang, f"--target={TARGETS[architecture]}", "-ffreestanding",
             "-fno-stack-protector", "-O2", *definitions, "-c",
             str(ROOT / "examples" / "native" / source), "-o", str(output)],
            check=True,
        )

    helper_definition = destination / "helper.def"
    helper_definition.write_text(
        "LIBRARY helper\nEXPORTS\n  Helper @7\n  NewHelper @8\n  OrdinalHelper @9 NONAME\n",
        encoding="ascii",
    )
    compile_source("helper.c", destination / "helper.obj")
    subprocess.run(
        [linker, "/dll", "/noentry", "/nodefaultlib", "/Brepro",
         f"/out:{destination / 'helper.dll'}", f"/implib:{destination / 'helper.lib'}",
         f"/def:{helper_definition}", str(destination / "helper.obj")],
        check=True,
    )

    exports = ["Add @1", "Subtract @2", "Hidden @9 NONAME"]
    variants = {
        "baseline": (exports, 0, False),
        "compatible": (exports + ["Multiply @3"], 0, False),
        "breaking": (["Add @1", "Multiply @3", "Hidden @9 NONAME"], 0, False),
        "renumbered": (["Add @2", "Subtract @1", "Hidden @9 NONAME"], 0, False),
        "forward-old": (exports + ["Forward=helper.Helper @4"], 0, False),
        "forward-new": (exports + ["Forward=helper.NewHelper @4"], 0, False),
        "eager": (exports, 1, False),
        "delay": (exports, 1, True),
        "import-added": (exports, 2, False),
    }
    for name, (symbols, use_helper, delay) in variants.items():
        folder = destination / name
        folder.mkdir(exist_ok=True)
        definition = folder / "sdk.def"
        definition.write_text("LIBRARY sdk\nEXPORTS\n  " + "\n  ".join(symbols) + "\n", encoding="ascii")
        object_file = folder / "sdk.obj"
        compile_source("sdk.c", object_file, f"-DUSE_HELPER={use_helper}", f"-DDELAY_STUB={int(delay)}")
        command = [
            linker, "/dll", "/noentry", "/nodefaultlib", "/Brepro",
            f"/out:{folder / 'sdk.dll'}", f"/implib:{folder / 'sdk.lib'}",
            f"/def:{definition}", str(object_file),
        ]
        if use_helper:
            command.append(str(destination / "helper.lib"))
        if delay:
            command.append("/delayload:helper.dll")
        subprocess.run(command, check=True)
    print(f"Built {len(variants)} {architecture} SDK fixtures in {destination}")
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=[*TARGETS, "all"], default="x64")
    arguments = parser.parse_args()
    for arch in TARGETS if arguments.arch == "all" else [arguments.arch]:
        build(arch)
