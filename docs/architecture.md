# Architecture and decisions

SDK Release Guard is a small, offline analysis pipeline:

```text
old DLL ----> bounded PE parser ----> normalized baseline --+
                                                          +--> comparison rules --> policy gate
new DLL ----> bounded PE parser ----> normalized candidate -+                           |
                                                                                 text / JSON / SARIF
```

## Layout

| Location | Responsibility |
|---|---|
| `src/SdkReleaseGuard.Core/PeSnapshotReader.cs` | PE headers, safe RVA mapping, exports and imports |
| `src/SdkReleaseGuard.Core/ReleaseComparer.cs` | Deterministic findings and gate policy |
| `src/SdkReleaseGuard.Core/Rules.cs` | Stable IDs, severity, explanation, remediation |
| `src/SdkReleaseGuard.Core/ReportWriter.cs` | Text, versioned JSON, SARIF serialization |
| `src/SdkReleaseGuard.Cli/` | Argument validation, filesystem I/O, exit codes |
| `tests/SdkReleaseGuard.Tests/` | Synthetic PE fixtures and focused regression tests |
| `examples/native/` | Original C source for independently compiled PE fixtures |
| `scripts/verify-native.py` | Real-process checks, LLVM comparison, schema validation |

## Decision: .NET and platform PEReader

.NET 10 provides cross-platform tooling and an established PE/COFF header
reader. The core library needs no third-party runtime packages. System.CommandLine
handles CLI parsing and help instead of a bespoke argument parser.

PEReader supplies headers and section metadata. A small bounded reader handles
exports and imports because the contract needed here includes ordinal aliases,
forwarders, delay descriptors, and strict failure semantics. It uses checked
arithmetic and validates every table span before reading.

The tradeoff is requiring .NET 10 rather than distributing self-contained
executables. v1 packages a portable .NET tool; standalone releases are not
claimed or required.

## Decision: fail closed on unreadable analysis data

An exploratory viewer can display a partially parsed binary with warnings.
A release gate must not convert a failed export-table read into "no exports."
Unsupported or malformed analyzed structures therefore stop the comparison
with exit `2`. Input failures do not produce success-shaped JSON or SARIF.

## Decision: separate facts, findings, and policy

`BinarySnapshot` contains normalized facts and a SHA-256 hash of the exact
input bytes. `ReleaseComparer.Compare` returns stable findings separately from
the selected fail threshold. Advisory mode still preserves all findings.
Readers of the JSON report can inspect `passed`, `failOn`, and the evidence
without interpreting terminal text.

The public library accepts snapshots produced by `PeSnapshotReader`; callers
constructing snapshots themselves are responsible for the same invariants
(unique ordinals/names, normalized modules, valid forwarders, initialized arrays).
The CLI never accepts user-authored snapshot JSON as a trusted baseline.

## Decision: preserve lookup semantics

An export may have multiple names or none. Names are case-sensitive; ordinal
slots are separate contracts. Export holes are not real exports. RVAs are not
stable symbol identities, so moving a function in a binary is not a finding.

Import module names are normalized to lowercase, but names and ordinals remain
distinct symbol kinds. Repeated module descriptors are merged without losing
symbols. Eager and delayed loading are intentionally distinct requirements.

## Decision: reproducible reports, limited path disclosure

Findings are ordered by severity, rule ID, and subject using ordinal string
comparison. JSON and SARIF have no generated timestamp or absolute input path.
Reports identify input bytes by hash. SARIF fingerprints use rule ID and subject
so they stay stable across rebuilds of the same affected contract.

Output files are written to a sibling temporary file and moved into place
without overwrite. This prevents partial success reports and protects input
files even if an output name is accidentally reused.

## Validation and residual risk

Unit tests exercise parser boundaries and rule semantics. A seeded mutation
campaign checks malformed images do not leak unchecked parsing exceptions.
Independent LLVM tools compile x86/x64/ARM64 DLLs and inspect exports, forwarders,
and imports. The native verification script compares their output to snapshots,
checks actual process exit codes, and validates both JSON and official SARIF
schemas. CI also installs and invokes the packaged tool.

Synthetic tests alone can share implementation mistakes; LLVM agreement is an
independent check, not a proof of all PE behavior. Neither the mutation campaign
nor these small fixtures is exhaustive fuzzing or a production adoption claim.

## References

- [PE/COFF format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
- [PEReader API](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader)
- [SARIF 2.1.0 specification](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html)
- [JSON report schema](report-schema.json)
