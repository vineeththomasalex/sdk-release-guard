# Rule reference

Rules describe changes from **baseline (old)** to **candidate (new)**.
All findings have a stable ID and a subject identifying the affected contract.
There are no automatic suppressions in v1.

| ID | Severity | Meaning |
|---|---|---|
| [SRG001](#srg001) | Breaking | CPU architecture changed |
| [SRG002](#srg002) | Breaking | PE32/PE32+ format changed |
| [SRG101](#srg101) | Breaking | Exported name removed |
| [SRG102](#srg102) | Breaking | Named export moved to another ordinal |
| [SRG103](#srg103) | Breaking | Populated export ordinal removed |
| [SRG104](#srg104) | Warning | Export forwarder changed |
| [SRG105](#srg105) | Info | Exported name added |
| [SRG106](#srg106) | Info | Populated export ordinal added |
| [SRG201](#srg201) | Warning | Import dependency added |
| [SRG202](#srg202) | Warning | Imported symbol added to an existing dependency |
| [SRG203](#srg203) | Warning | Delay-only dependency made eager |
| [SRG204](#srg204) | Info | Import dependency removed |

## SRG001

The target changed between x86, x64, or ARM64. Existing consumers generally
cannot substitute a library built for a different CPU architecture. Compare
matching targets or publish separate assets; architecture changes are not
silently treated as unrelated inputs.

## SRG002

The image changed between PE32 and PE32+. For supported architectures this
usually accompanies SRG001. Both dimensions are reported explicitly.

## SRG101

A case-sensitive export name disappeared, including an alias when another
alias still exists at the same ordinal. Consumers resolving that name may fail
to load or obtain its address. Restore the exact name or a compatible shim.

## SRG102

A named export moved from ordinal N to ordinal M. Name-based lookup might
still work, but ordinal-based consumers can fail or silently bind to something
else. Explicitly preserve ordinals in the library's module-definition file.
Swapping two ordinals produces two findings even when neither name disappears.

## SRG103

A nonzero export-address-table slot disappeared, including a NONAME export.
Zero entries are holes, not exports. A named export removal can produce both
SRG101 and SRG103 because names and ordinals are distinct lookup contracts.
Changing code behind a still-populated, unnamed ordinal is not detectable.

## SRG104

A surviving named export's forwarding target changed, or it switched between
a local implementation and a forwarder. Unnamed exports are compared by ordinal.
The module portion is case-insensitive; the target symbol or `#ordinal` portion
is compared exactly. An equivalent forwarding shim may be safe, so this is a
warning rather than a definite break. Validate the destination and behavior
on supported systems. Forwarder modules are not included in the import table.

## SRG105

A new name is exported. Adding an alias also produces this informational
finding. The presence of new APIs says nothing about unchanged APIs' behavior.

## SRG106

A previously absent export ordinal is now populated. A single added API may
produce both SRG105 and SRG106. Preserve the ordinal in future releases if
ordinal-based consumers are supported.

## SRG201

A module is newly imported in eager or delay-loaded mode. The modes are
separate requirements: introducing a delayed import is still relevant, even
when the module is already imported eagerly. One finding represents the whole
new module requirement; its individual symbols do not also produce SRG202.
Check deployment and supported platform versions. API-set names are compared
as written, without resolving them against an OS installation.

## SRG202

An existing module/mode pair imports a new case-sensitive symbol name or
ordinal. This can raise the minimum dependency version even though the DLL
name did not change. A literal name such as `#7` is distinct from ordinal 7.
Import removals alone do not produce symbol-level findings in v1.

## SRG203

A module was imported only in delayed mode and is now imported eagerly.
It must now be available at load time. If the delayed descriptor also
disappears, SRG204 is emitted as well. If the module was already eager,
SRG203 is not emitted.

## SRG204

A module/mode pair is no longer imported. This is informational; the dependency
could still be used by a forwarder, a runtime loader call, another dependency,
or an application. Do not remove deployment assets solely because of this rule.

## Gate policy

`--fail-on breaking` fails on breaking findings. `--fail-on warning` fails on
breaking or warning findings. `--fail-on none` is advisory; it does not hide
findings or turn analysis errors into success. Info never fails a gate.

Do not infer a semantic-version bump automatically from finding counts.
A planned breaking release needs a human migration and versioning decision.
