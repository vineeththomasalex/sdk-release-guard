# Use the CLI in GitHub Actions

First build or retrieve two DLLs from **trusted, traceable releases**.
The example assumes your workflow has already placed them at
`previous/sdk.dll` and `current/sdk.dll` in the workspace.

SDK Release Guard is not published to NuGet yet, so the example builds the
tool from source. For a production gate, replace the tool checkout's `main`
ref with a reviewed immutable commit SHA. Review updates before changing it.

The checkout paths below keep the analyzer separate from your application.
Add these steps to a job on an Ubuntu runner with `contents: read` permission:

```yaml
- uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4

# Your steps here: obtain previous/sdk.dll and build current/sdk.dll.

- uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4
  with:
    repository: vineeththomasalex/sdk-release-guard
    ref: main # Replace with a reviewed SDK Release Guard commit SHA.
    path: .release-guard

- uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4
  with:
    dotnet-version: "10.0.x"

- name: Build release guard
  working-directory: .release-guard
  run: |
    dotnet restore --locked-mode
    dotnet build -c Release --no-restore

- name: Check native release contract
  id: guard
  shell: bash
  run: |
    set +e
    dotnet .release-guard/src/SdkReleaseGuard.Cli/bin/Release/net10.0/sdk-release-guard.dll \
      compare --baseline previous/sdk.dll --candidate current/sdk.dll \
      --format sarif --output release-report.sarif
    status=$?
    set -e
    echo "exit_code=$status" >> "$GITHUB_OUTPUT"
    if [ "$status" -eq 1 ]; then
      echo "::error::Release findings failed the gate. Inspect the report artifact."
    elif [ "$status" -ne 0 ]; then
      echo "::error::Release analysis failed; this is not a compatibility result."
    fi
    exit "$status"

- name: Preserve the report even when findings fail the gate
  if: ${{ always() && (steps.guard.outputs.exit_code == '0' || steps.guard.outputs.exit_code == '1') }}
  uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4
  with:
    name: native-release-report
    path: release-report.sarif
    if-no-files-found: error
```

The gate step intentionally fails on exit `1` or `2`. Do not add
`continue-on-error` or `|| true` to make the workflow green. Uploading the report
does not undo a failed gate.

For JSON, change the format and filename and consume
[`schemaVersion: "1.0"`](report-schema.json). To require review of dependency
and forwarding changes too, add `--fail-on warning`.

## SARIF viewer notes

The report uses native DLL artifact locations and logical export/import
subjects, not invented source lines. File URIs are escaped basenames; absolute
build-machine paths are not embedded. Some source-oriented code-scanning
viewers will not annotate generated or untracked binaries. Schema-valid SARIF
does not guarantee a particular hosting service will display source annotations.
The uploaded artifact is always the portable report to review.

Use distinct artifact names for an architecture matrix. Keep DLLs and reports
private when the SDK is proprietary; uploading a workflow artifact is still
data publication to the repository's authorized readers.
