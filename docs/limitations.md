# Scope, guarantees, and limitations

## What a successful comparison means

Both inputs were parsed for the supported static PE surface, and findings were
evaluated against the chosen threshold. It **does not** mean either DLL is safe
to execute, trusted, complete, or ABI/behaviorally compatible.

Even a byte-for-byte identical library can fail in a changed deployment
environment. Static metadata is one release signal, not a substitute for
consumer integration tests.

## Supported inputs

- Native Windows DLLs using PE32/x86 or PE32+/x64/ARM64.
- Named and ordinal-only exports, aliases, export holes, and forwarders.
- Ordinary import lookup tables, unbound IAT fallback, and delay imports.
- Both RVA-based and legacy VA-based delay-import metadata when representable.
- Data exports in a section's zero-filled tail; metadata itself must be
  backed by file bytes.

An input can be analyzed on a different host OS or CPU. DLL filenames do not
need a particular extension, but the PE headers must identify a native DLL.

## Explicitly unsupported

- ELF, Mach-O, COFF object files, EXEs, .NET assemblies, and mixed-mode binaries.
- Hybrid ARM64EC/ARM64X and architectures outside x86, x64, and ARM64.
- Overlapping sections or metadata tables that cross file-backed section
  boundaries. Some unusual images accepted by a loader may be rejected here.
- Bound imports without a recoverable lookup table. A bound address is not
  silently interpreted as an import name.
- Non-printable/non-ASCII metadata names.

Unsupported or malformed analysis inputs return exit `2`, not an empty
successful report. The parser is strict about the structures it reads; it is
not a full Windows-loader validity checker for every unrelated PE directory.

## Important blind spots

PE export/import tables do not establish:

- Parameter types, calling conventions, C++ class/vtable layout, packing,
  allocation ownership, or data export sizes.
- Behavior, threading guarantees, error semantics, or performance.
- The identity of a new implementation behind an unchanged unnamed ordinal.
- Dynamic `LoadLibrary`/`GetProcAddress` dependencies or transitive dependencies.
- The availability of an imported symbol on the oldest supported OS.
- API-set resolution, complete deployment manifests, resources, or side-by-side
  activation behavior.
- Signing/certificate trust, security mitigations, or absence of malicious code.

Changed RVAs, timestamps, code bytes, and checksums are deliberately ignored
as compatibility signals. Adding a local implementation in place of a
forwarder may be compatible; SRG104 asks for review rather than proving a break.

## Analysis limits

To bound work on untrusted inputs, each image is limited to:

| Limit | Value |
|---|---|
| File size | 128 MiB |
| Sections | 1 through 96, non-overlapping |
| Combined table-entry budget | 100,000 |
| One metadata name | 4,096 printable ASCII bytes |
| Total decoded-name budget | 16 MiB |

The entry budget includes export slots (including holes), export names,
non-null import descriptors, and import symbols. Repeated string references
still consume the decoded-name budget. Exceeding a limit is an explicit
analysis error; there is no truncation that could hide findings.

Files are never executed or uploaded by the analyzer. Reports contain symbol
and dependency names, input hashes, and basenames: **treat reports as sensitive
if the analyzed SDK is proprietary**. Only publish reports you are allowed to
share. Error diagnostics may include the input path supplied by the caller.

## Recommended release process

Use the gate alongside tests that build and run representative consumers
against both releases, explicit API/ABI contract reviews, deployment checks,
and a human decision on intentional breaking changes. Keep baseline artifacts
immutable and traceable to a known release.
