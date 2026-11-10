# Try a real release comparison

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[Python 3.10+](https://www.python.org/downloads/), and
[LLVM](https://releases.llvm.org/) with `clang` and `lld-link` on PATH.
LLVM cross-compiles all three targets on Windows, Linux, or macOS; a Windows SDK
is not required. Run these commands from the repository root.

## 1. Build the tool and example DLLs

```shell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
python scripts/build-fixtures.py
```

The script compiles this repository's C sources into
`artifacts/fixtures/x64/`. It does not copy installed DLLs or download binaries.
For other architectures, use `--arch x86`, `--arch arm64`, or `--arch all`.
On systems that name Python `python3`, substitute `python3` for `python`.

## 2. Compare an additive release

```shell
dotnet src/SdkReleaseGuard.Cli/bin/Release/net10.0/sdk-release-guard.dll compare -b artifacts/fixtures/x64/baseline/sdk.dll -c artifacts/fixtures/x64/compatible/sdk.dll
```

Expected: **exit 0**, `Gate: PASS`, and two informational findings (`SRG105`,
`SRG106`) for the new `Multiply` name and ordinal. Existing exports remain.

## 3. Compare a breaking release

```shell
dotnet src/SdkReleaseGuard.Cli/bin/Release/net10.0/sdk-release-guard.dll compare -b artifacts/fixtures/x64/baseline/sdk.dll -c artifacts/fixtures/x64/breaking/sdk.dll
```

Expected: **exit 1**, `Gate: FAIL`, two breaking findings (`SRG101`, `SRG103`)
for the removed `Subtract` name and ordinal, and two informational findings for
`Multiply`. A nonzero exit here is intentional; the tool worked.

PowerShell: inspect `$LASTEXITCODE` immediately after the command.
Bash: inspect `$?` immediately after the command.

## 4. Inspect a dependency warning

```shell
dotnet src/SdkReleaseGuard.Cli/bin/Release/net10.0/sdk-release-guard.dll compare -b artifacts/fixtures/x64/delay/sdk.dll -c artifacts/fixtures/x64/eager/sdk.dll --fail-on warning
```

Expected: **exit 1**, one `SRG203` warning and one `SRG204` informational finding.
The helper DLL is now required at load time rather than first use.

## Fixture inventory

| Variant | Difference from `baseline` |
|---|---|
| `baseline` | Named exports `Add` at 1 and `Subtract` at 2; NONAME export at 9 |
| `compatible` | Adds `Multiply` at 3 |
| `breaking` | Removes `Subtract`/2 and adds `Multiply`/3 |
| `renumbered` | Swaps the ordinals of `Add` and `Subtract` |
| `forward-old` | Adds `Forward` at 4 targeting `helper.Helper` |
| `forward-new` | Same as `forward-old`, but targets `helper.NewHelper` |
| `eager` | Imports `Helper` by name and helper ordinal 9 eagerly |
| `delay` | The same imports are delay-loaded |
| `import-added` | Same as `eager`, additionally imports `NewHelper` |

These are **analysis fixtures, not runnable sample SDKs**. They have no entry
point or runtime initialization, and the delay-load helper is a link-only stub.
Do not use the generated DLLs in applications. Neither the CLI nor the
verification scripts load them.

To independently inspect one file:

```shell
llvm-readobj --coff-exports --coff-imports artifacts/fixtures/x64/eager/sdk.dll
```

For automated cross-checks and report-schema validation, follow
[the contribution guide](../CONTRIBUTING.md).
