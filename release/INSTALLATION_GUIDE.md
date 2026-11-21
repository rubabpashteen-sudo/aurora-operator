# Aurora Operator 0.2.0 — Installation Guide (for ordinary Windows users)

No programming needed. You do not need Visual Studio, the command line, or
any developer tools. You only need a Windows 10 or Windows 11 computer
(64-bit), about 6 GB of free disk space, and an internet connection for the
first install (the AI model is downloaded once; afterwards the app works
offline for local tasks).

## 1. Which file do I download?

Download **`AuroraOperator-Setup-0.2.0-x64.exe`** (about 105 MB).

- **SHA-256 checksum:** `a185abb1e47303a7801f1b45c694b2aef3b86777ef16e54b0ee590523c807f97`
- Before running it, check the checksum if you can: in PowerShell, run
  `Get-FileHash .\AuroraOperator-Setup-0.2.0-x64.exe -Algorithm SHA256`
  and compare it with the value above. If it does not match, do not run it.
- The portable ZIP (`AuroraOperator-Portable-0.2.0-x64.zip`) is only for
  advanced users: unzip it and run `AuroraOperator.exe` inside. It does not
  install Ollama or a model for you.

## 2. How do I install it?

1. Double-click `AuroraOperator-Setup-0.2.0-x64.exe`.
2. **SmartScreen warning:** the installer is not digitally signed, so
   Windows will show a blue "Windows protected your PC" warning. This is
   expected for this unsigned release candidate. Only continue if you
   verified the SHA-256 above. Click **More info**, then **Run anyway**.
3. Click through the installer. It asks for administrator permission —
   that is normal: it installs into Program Files and installs Ollama.
4. Wait. The installer will:
   - check your Windows version and free disk space,
   - download and install **Ollama** automatically if you do not have it
     (you do **not** need to install Ollama yourself),
   - download the AI model for your computer's memory (below 8 GB:
     `qwen3:0.6b`; 8 GB: `qwen3:1.7b`; 16 GB: `qwen3:4b`; 32 GB:
     `qwen3:8b`). This is the slow part — a few minutes depending on
     your connection. Do not close the installer while it downloads.
   - install Aurora Operator, create Start Menu and desktop shortcuts,
     and run a health check.
5. If anything essential fails (for example the model cannot be
   downloaded), the installer **cancels and removes the app files**
   instead of pretending it worked. In that case, check your internet
   connection and try again.
6. At the end, tick **Launch Aurora Operator** and click Finish.

## 3. Do I need Ollama or a model already?

No. The installer installs Ollama and downloads one model for you. If you
already have Ollama, the installer finds it and skips the download.

## 4. How do I open Aurora Operator?

Use the **Aurora Operator** shortcut on your desktop or in the Start Menu.
The first window shows the chat, a mode selector (Ask / Plan / Autonomous)
and a model indicator at the top.

## 5. How do I check the local AI is working?

1. Look at the top bar: it should say **Local AI: connected** and show the
   model name (for example `qwen3:1.7b`).
2. Type: `What can you help me with?` and press Enter. You should get an
   answer written by the local model — no internet is needed for this once
   the model is downloaded.
3. If it says Ollama is not connected, check that Ollama is running: open
   PowerShell and run `ollama list`. You should see your model listed.
   If Ollama is not running, start it from the Start Menu (Ollama) and
   restart Aurora Operator.

## 6. First test: Notepad

This checks that the agent can really operate your computer.

1. Set the mode to **Autonomous** (or **Plan** if you want to approve each
   step first).
2. Type exactly: `Open Notepad and write Hello World`
3. Press Enter.
4. Notepad should open and the text `Hello World` should appear in it.
   The agent reads the text back and tells you whether it matches.
5. Now try saving it: type `Save the Notepad text to a file on my Desktop
   called hello.txt` and check that the file exists and contains
   `Hello World`.

If the agent asks for approval, read what it wants to do, then click
**Approve** or **Deny**. Denying is always safe — nothing runs.

## 7. A second test: find your PDFs

Type: `Find all PDF files in my Downloads folder and list them`
The agent should list real files from your computer, not invented ones.

## 8. Where are logs and diagnostics?

- App data, task history and logs:
  `%LOCALAPPDATA%\AuroraOperator` — paste that into File Explorer's
  address bar. Logs are in the `logs` folder; the database is
  `aurora.db`.
- Windows diagnostics script (for reporting a problem):
  `tests\windows\windows-diagnostics.ps1` in the source folder, or run
  it from PowerShell. It only reads information and writes a report —
  it changes nothing.
- If something goes wrong, copy the newest log file and the exact steps
  you took, and send them to the developer. Never paste passwords into
  the app or into a bug report.

## 9. How do I uninstall it?

- **Start Menu → Aurora Operator → Uninstall Aurora Operator**, or
  Windows Settings → Apps → Aurora Operator → Uninstall.
- The uninstaller asks whether to **keep or remove the downloaded AI
  models**. Ollama itself stays installed (you may use it for other
  things). Your Aurora Operator data in
  `%LOCALAPPDATA%\AuroraOperator` is kept, so reinstalling later
  restores your tasks and memory. Delete that folder manually if you
  want everything gone.

## 10. If the install fails

- **"Not enough free disk space"** — free up space (you need the app
  plus the model: roughly 1–6 GB depending on your RAM) and try again.
- **Model download fails** — check your internet connection and try
  again; the download resumes. If it fails three times, the installer
  cancels cleanly.
- **Ollama did not start** — restart your computer once and try again;
  another program may already be using port 11434.
- **SmartScreen / "unknown publisher"** — expected: this build is
  unsigned. Verify the SHA-256 in step 1 before choosing Run anyway.
