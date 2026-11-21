# Publishing Aurora Operator to GitHub

The code in this folder is published on GitHub:

**https://github.com/rubabpashteen-sudo/aurora-operator** (public, `main` branch)

The first snapshot (92 text files) was pushed 2026-11-22 00:32 PKT as
commit `2d0748143522f1fa42a3b05a62bd59709f79568f`, verified by reading
files back from GitHub. Updates are pushed the same way (see below).
Your Codex can clone it with:

`git clone https://github.com/rubabpashteen-sudo/aurora-operator.git`

## Why this file exists

The Muse GitHub connection in this workspace can read repositories and
push files to repositories the Muse GitHub App is installed on, but it
**cannot create a new repository**: `create_repository` fails with
`403 Resource not accessible by integration`. The repository was
therefore created on github.com (via the browser, signed in as
rubabpashteen-sudo) and the code was pushed to it as a snapshot.

## Pushing updates (from this machine)

Muse pushes updates through its GitHub connector, following
`/opt/hatch/connectors/github/references/snapshot-push.md` exactly:

1. Build the NUL-delimited manifest of tracked + unignored untracked
   files (excluding sync records) with `git ls-files`.
2. Recheck the `main` branch head with `list_commits` first.
3. Push with `github push-files --owner rubabpashteen-sudo --repo
   aurora-operator --branch main --message "..." --source-dir
   <absolute path> --paths-file <manifest>`.
4. Record the confirmed commit SHA in the adjacent sync record
   `<source-dir>.muse-github-snapshot-sync.json`.
5. Verify afterwards by reading at least one changed file back from
   GitHub.

Do NOT use `git push` with a token: there is no GitHub login on this
machine (`gh` is not authenticated) and no separate auth flow may be
started — the connector's push-files route is the supported path.

## Notes

- **Binaries are intentionally not in git.** The compiled installer
  (`release/*.exe`, `release/*.zip`), `publish/`, `bin/` and `obj/`
  are excluded by `.gitignore`. The app icon files
  (`src/AuroraOperator.App/Assets/aurora.ico` and `aurora.png`) are
  also excluded because they are binary; they are regenerated from
  the tracked vector source with:
  `python3 src/AuroraOperator.App/Assets/build-icon.py`
  Run that once after cloning, before `dotnet build`, or the WPF
  build will fail on the missing icon.
- The installer itself is delivered separately (Google Drive link in
  `release/`); GitHub holds source code only.
- The tree was verified to contain no credentials, tokens, API keys or
  personal data. Public is the visibility that guarantees any Codex can
  read it without knowing how that Codex authenticates.
