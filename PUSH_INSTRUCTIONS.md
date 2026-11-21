# Publishing Aurora Operator to GitHub

The code in this folder is ready to push. It is already a git repository
(`main` branch, one commit) with a `.gitignore` that keeps binaries,
build outputs and local data out of the tree.

## Why this file exists

The Muse GitHub connection in this workspace can read repositories and
push files to repositories the Muse GitHub App is installed on, but it
**cannot create a new repository**: `create_repository` fails with
`403 Resource not accessible by integration`. Creating the empty
repository is therefore the one step that has to happen on github.com
(about one minute). Everything after that can be done from here.

## Step 1 — Create the empty repository (on github.com)

1. Go to https://github.com/new and sign in as
   **rubabpashteen-sudo** (the account the code will live under).
2. Repository name: `aurora-operator`
3. Description: `Aurora Operator — a local-first Windows desktop AI agent (.NET 8 WPF + Ollama).`
4. Visibility: **Public** is recommended. The tree was verified to
   contain no credentials, tokens, API keys or personal data, and
   public is the only visibility that guarantees any Codex can read it
   without knowing how that Codex authenticates. Choose Private only
   if your Codex signs in as the same GitHub account.
5. Do **not** initialise it with a README, licence or .gitignore —
   leave it completely empty.
6. Create the repository.

## Step 2 — Let Muse (and Codex) reach it

- If you want Muse to push updates to it later, install the Muse
  GitHub App on the new repository (GitHub → Settings → Applications
  → Muse → Repository access → select `aurora-operator`).
- Your Codex can then clone it with:
  `git clone https://github.com/rubabpashteen-sudo/aurora-operator.git`
  (public), or via its own GitHub connection if you keep it private.

## Step 3 — Push this tree (from this machine)

```sh
cd ~/workspace/aurora-operator
git remote add origin https://github.com/rubabpashteen-sudo/aurora-operator.git
git push -u origin main
```

That pushes the local `main` commit (91 text files). The intended URL
afterwards is: https://github.com/rubabpashteen-sudo/aurora-operator

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
- If you would rather have Muse push the files through its GitHub
  connector instead of the commands above, create the empty repo as
  in Step 1, install the Muse GitHub App on it as in Step 2, and ask
  Muse to push the snapshot — it will verify the repo is empty first
  and then upload the same 91 files.
