# GamerSense release workflow

Preserve the C# audio pipeline and remembered settings when making unrelated changes.
Validate the affected behavior, keep BUILD-DEBUG.bat, and produce the usual App
and Source ZIPs plus guide before publishing a version. Use native pre-volume
audio for analysis and exported training samples.

The user requested that each new GamerSense version also be published to
TPavZ/GamerSense while retaining the existing downloadable ZIP format. Follow
docs/PUBLISHING.md and scripts/publish_release.py for requested release work.
Upload the exact prepared ZIPs, verify the release asset hashes, and provide
local download links plus the GitHub release link. Do not replace old tags or
assets, force-push, or silently publish raw learning collections.

The opt-in category preview adjusts mixed sections; do not describe it as
separated source audio. Classifier suggestions do not become approved labels
without human review. Report listening/recognition limitations candidly.
