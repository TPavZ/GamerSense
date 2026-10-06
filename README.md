# GamerSense

Windows game audio passthrough, live spectrum monitoring, captured-sound review,
and an opt-in preview of category volume control.

[Download the latest release](https://github.com/TPavZ/GamerSense/releases/latest)
· [All historical versions](https://github.com/TPavZ/GamerSense/releases)
· [Release file hashes](docs/releases.json)

## Downloads

Each release keeps the downloadable format used during local development:

- `GamerSense-vX.Y.Z-app.zip`: extract the whole ZIP into a new folder, then open GamerSense.exe.
- `GamerSense-vX.Y.Z-source.zip`: source project, BUILD-DEBUG.bat, and available checks.
- The version guide, when available, and SHA256SUMS.txt for file verification.

The original App and Source ZIP files are uploaded unchanged. The automatically
generated GitHub source-code downloads are separate from these named assets.
v0.1.0 preserves the original repository state as source only; v0.2.0 includes
a recovered local Debug app build and source snapshot. Later archived releases
retain their original packaged builds. These are development versions.

## Current behavior

Select game/virtual audio input and the true output device. Low-delay direct
cable capture uses CABLE Output as the recording input. Device choices and
playback mode are remembered. Captured spikes can be replayed, labeled, trimmed,
approved/exported or deleted. Unreviewed clips have a one-hour lifetime and
a 2 GB temporary storage budget; approved exports are kept separately.

Volume controls include Overall, Explosions, Footsteps, Ground vehicles and Air
vehicles. Category routing starts off each launch. This preview changes an
estimated mixed section, including any overlapping sounds; it does not isolate
individual sources. Suggestions are tentative and require review. Captured
training samples retain the original pre-volume audio.

## Build

Windows 10/11 and the .NET 9 SDK are required to build. Run BUILD-DEBUG.bat,
or `dotnet build src/GamerSense/GamerSense.csproj -c Debug`. Running the packaged
app requires the .NET 9 Desktop Runtime.

Automated checks:

```powershell
dotnet run --project tests/AnalyzerChecks/AnalyzerChecks.csproj
dotnet run --project tests/ReviewUiChecks/ReviewUiChecks.csproj
```

Real-device audio quality, live classification usefulness and added processing
cost still require listening tests. Frequency similarity is not source separation.

## Historical snapshots and future publishing

The original Git history is preserved. Each recovered version adds a source
snapshot commit and annotated version tag. Snapshot import dates do not pretend
to be the original development dates. Native source-file bytes are verified
against the saved source ZIPs; generated bin/obj folders are excluded from Git.

See [the publishing guide](docs/PUBLISHING.md). New releases continue to provide
local App/Source ZIP downloads and upload those same files to GitHub Releases.
The publisher verifies SHA-256 hashes, refuses to replace conflicting assets,
and pushes without rewriting remote history. Raw approved-clip collections and
offline learning datasets are separate from these versioned app packages.
