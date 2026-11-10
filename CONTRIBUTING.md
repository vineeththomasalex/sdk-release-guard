# Contributing

Start with the [architecture](docs/architecture.md) and
[limitations](docs/limitations.md). Small, well-tested improvements to supported
native PE analysis are welcome. Discuss a new platform, input format, or policy
system in an issue before implementing it.

## Build and test

Prerequisite: .NET 10 SDK.

```shell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

For an intentional dependency change, run `dotnet restore` without locked
mode and commit the updated `packages.lock.json` files. Do not edit them by
hand. `NuGet.config` uses the public NuGet feed; do not commit private feed
URLs or credentials. If your network requires a public package mirror, pass
it to `dotnet restore --source <mirror-url>` locally rather than changing the
repository configuration.

## Native integration checks

Install Python 3.10+ and LLVM (`clang`, `lld-link`, and `llvm-readobj` on PATH).
The compiler emits Windows binaries without a Windows SDK on any supported
host. The DLLs are never executed.

Create a Python virtual environment with `python -m venv .venv`.
Activate it with `.venv\Scripts\Activate.ps1` in PowerShell, or
`source .venv/bin/activate` in bash.

```shell
python -m pip install -r scripts/requirements-test.txt
python scripts/build-fixtures.py --arch all
python scripts/verify-native.py
```

The verifier requires a Release build. It checks ordinary, delay, name and
ordinal imports; named and NONAME exports; forwarders; architecture changes;
CLI gates; and schema-valid JSON/SARIF. It downloads the SARIF schema from a
pinned SchemaStore revision into ignored `artifacts/schemas/` on its first run.
Network access is needed for dependency installation and that first schema
download, not for the analyzer itself.

To test the distributable package:

```shell
dotnet pack src/SdkReleaseGuard.Cli -c Release --no-build -o artifacts/packages
dotnet tool install SdkReleaseGuard --version 0.1.0 --tool-path artifacts/tool --add-source artifacts/packages
```

Then run:

- PowerShell: `python scripts/verify-native.py --tool artifacts\tool\sdk-release-guard.exe`
- Bash: `python scripts/verify-native.py --tool artifacts/tool/sdk-release-guard`

Use `dotnet tool update` instead of `install` if that tool directory already
contains an installation. For rebuilding an unchanged package version locally,
uninstall from that isolated tool directory first and install with `--no-cache`.

## Adding a rule or fixing a parser bug

Add a regression test that fails before the fix. For a rule, cover both the
actual risk and the nearest harmless change. Keep detection independent from
formatting. Add a stable rule ID, severity, explanation, and remediation to
`Rules.cs`, then update `docs/rules.md`.

Prefer a source-built native fixture when behavior depends on linker output;
use the synthetic builder for precise malformed-input boundaries. Avoid
silently ignoring unknown data or returning an empty snapshot after errors.

Do not commit generated binaries, package files, private SDKs, system DLLs,
proprietary traces, or claims of adoption/performance that have not been
measured. Use public facts or independently authored examples.
