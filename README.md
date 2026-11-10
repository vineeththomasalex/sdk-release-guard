# SDK Release Guard

**Catch native Windows DLL release risks before downstream users do.**

[![CI](https://github.com/vineeththomasalex/sdk-release-guard/actions/workflows/ci.yml/badge.svg)](https://github.com/vineeththomasalex/sdk-release-guard/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

SDK Release Guard compares a previous DLL with a candidate release and reports
removed exports, changed ordinals, forwarding changes, architecture changes, and
new import requirements. Use readable terminal output locally and JSON or SARIF
in CI. Analysis runs on Windows, Linux, and macOS: input DLLs are read as data,
never loaded or executed.

**Status:** v0.1.0. Supports native x86, x64, and ARM64 PE DLLs. A passing gate
means no policy-blocking changes were found in the checked surface, **not** that
the release is ABI-compatible. See [limitations](docs/limitations.md).

## Install from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and Git. No Windows SDK or native compiler is needed to analyze your own DLLs.
This project is not yet published on NuGet.

```shell
git clone https://github.com/vineeththomasalex/sdk-release-guard.git
cd sdk-release-guard
dotnet restore --locked-mode
dotnet pack src/SdkReleaseGuard.Cli -c Release -o artifacts/packages --no-restore
dotnet tool install --global SdkReleaseGuard --version 0.1.0 --add-source artifacts/packages
sdk-release-guard --help
```

If the shell cannot find the command, add `$HOME/.dotnet/tools` (Linux/macOS) or
`%USERPROFILE%\.dotnet\tools` (Windows) to PATH and open a new terminal.
For an existing installation, use `dotnet tool update` instead of `install`.

## Compare your first release

`--baseline` is the old contract. `--candidate` is the proposed replacement.
Compare the same library for the same target architecture.

```shell
sdk-release-guard compare --baseline previous/sdk.dll --candidate current/sdk.dll
```

An intentionally breaking comparison looks like:

```text
[BREAKING] SRG101: Export "Subtract" was removed (previous ordinal 2).
  Action: Restore the exact exported name or provide a compatible forwarding shim.
[BREAKING] SRG103: Export ordinal 2 was removed.
  Action: Restore the ordinal, including NONAME exports, or deliberately version the binary contract.
```

These are separate name-based and ordinal-based consumer contracts, so one
change can produce both findings. Findings are risks to review, not a count of
distinct defects.

**No DLLs handy?** Follow the [source-built example](examples/README.md).
It produces both passing and deliberately breaking pairs without downloading
third-party binaries or requiring the Windows SDK.

## Reports and exit codes

```shell
sdk-release-guard compare -b previous/sdk.dll -c current/sdk.dll --format json --output release-report.json
sdk-release-guard compare -b previous/sdk.dll -c current/sdk.dll --format sarif --output release-report.sarif
sdk-release-guard compare -b previous/sdk.dll -c current/sdk.dll --fail-on warning
```

| Exit code | Meaning |
|---|---|
| `0` | Analysis completed and the selected gate passed; also used by help/version. |
| `1` | Analysis completed and findings failed the selected gate. The report is still emitted. |
| `2` | Invalid arguments, malformed/unsupported input, or an I/O failure. No successful analysis should be inferred. |

`--fail-on breaking` is the default. `warning` fails on breaking **or** warning
findings. `none` produces an advisory report and never fails on findings, but
input/output errors still return `2`. Informational findings never fail a gate.

Reports go to stdout unless `--output` is supplied; diagnostics go to stderr.
Output files are UTF-8, created atomically, and **must not already exist**.
Choose a new path or explicitly remove a previous report before rerunning.
The parent directory must exist. Input DLLs and existing reports are never
overwritten.

JSON includes the normalized snapshots, SHA-256 input hashes, stable rule IDs,
findings, threshold, and gate result under `schemaVersion: "1.0"`. It deliberately
records file basenames rather than absolute paths. The
[JSON schema](docs/report-schema.json) defines the report contract. SARIF uses version 2.1.0 and
maps breaking/warning/informational findings to error/warning/note.

## What it checks

- CPU architecture and PE32 versus PE32+.
- Export names (case-sensitive), public ordinals, aliases, and NONAME exports.
- Changed forwarders, including forwarding by ordinal.
- Ordinary and delay-loaded import dependencies.
- Added name/ordinal imports from existing dependencies.
- A previously delay-only dependency becoming eager.

Address relocation, code bytes, timestamps, and checksums are not compatibility
signals and do not generate findings. DLL dependency names are compared
case-insensitively; exported and imported symbol names are case-sensitive.

Read the [rule reference](docs/rules.md), [limitations](docs/limitations.md), and
[architecture decisions](docs/architecture.md) before relying on the gate.

## CI

See the [GitHub Actions integration](docs/ci.md) for a gate that preserves the
exit code, uploads its report even when findings fail the gate, and distinguishes
analysis errors from compatibility findings. You can also use the CLI from any
CI system with .NET 10.

## Development

```shell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for native-fixture checks and how to add a
rule. The core library has no third-party runtime dependencies. The CLI uses
System.CommandLine.

## License

[MIT](LICENSE). Sample DLLs are generated from this repository's small C sources;
no system DLLs, proprietary SDK files, or generated binaries are committed.
