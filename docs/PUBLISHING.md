# Publish GamerSense while keeping ZIP downloads

1. Finish the app change and validate it. Create the App and Source ZIPs in the
   existing download directory, retaining the `GamerSense-vX.Y.Z-app.zip` and
   `GamerSense-vX.Y.Z-source.zip` names and top-level GamerSense folder.
2. Keep the matching version guide there when available. Confirm the source
   project version agrees with X.Y.Z. BUILD-DEBUG.bat remains in the source ZIP.
3. Run the publisher from a clean checkout with Git and Python 3 on PATH:

```powershell
python scripts/publish_release.py --version X.Y.Z --assets-dir "C:\path\to\outputs"
```

This imports the source snapshot, creates a commit and annotated tag, pushes
without force, and publishes the exact existing ZIP files, guide and checksums.
Credentials come from an existing GitHub CLI or Git Credential Manager sign-in,
or standard GH_TOKEN/GITHUB_TOKEN environment variables. They are never printed,
written into the project or passed as command-line token arguments.

For a one-time sign-in, use `gh auth login` or
`git credential-manager github login --device --username TPavZ`. Follow GitHub's
authorization page. Do not paste access tokens into chats, source or scripts.

If remote main advances independently, reconcile the source changes first;
the publisher refuses a force push. Existing tags/assets with conflicting
content are never replaced. Releases are initially drafts and become public
only after every attached asset's GitHub SHA-256 and byte count match locally.

The history importer uses --manifest to publish the recovered local archive.
Its --check-only mode validates local tags and assets without remote writes.
Version guides remain the authority for each historical build's behavior.
Old versions are archived artifacts, not claims that current checks were run
against every older release. Original v0.1 has source only; v0.2's app is a
recovered retained Debug build, not a newly recreated historical executable.

Return both the usual local ZIP download links and the GitHub release link.
Do not place raw learning datasets, approved-clip folders, credentials or build
caches in Git. The public release manifest records asset names and hashes.
