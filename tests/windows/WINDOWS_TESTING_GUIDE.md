# Aurora Operator — Windows Testing Guide (Phase 7)

Manual Windows test pass for Aurora Operator. **Nothing in this guide has
been run on Windows yet: every item below is NOT VERIFIED until it is run on
a real Windows machine and recorded in
`WINDOWS_ACCEPTANCE_CHECKLIST.md`.** Do not claim Windows compatibility
until the checklist passes.

## What you need before you start

| Prerequisite | How to check |
|---|---|
| Windows 10 22H2 (build 19045+) or Windows 11, 64-bit | `winver` |
| 8 GB+ RAM for `qwen3:4b`; 4–8 GB → `qwen3:1.7b` | Task Manager → Performance → Memory |
| .NET 8 Desktop Runtime (or .NET 8 SDK) | `dotnet --list-runtimes` shows `Microsoft.WindowsDesktop.App 8.x` |
| Ollama installed, model pulled | `ollama list` shows `qwen3:4b` (or `qwen3:1.7b`) |
| Playwright Chromium (only for item 11) | `%LOCALAPPDATA%\ms-playwright\chromium-*` exists |
| Aurora Operator installed (or built from source) | `%ProgramFiles%\Aurora Operator\AuroraOperator.exe` exists |

Run the diagnostics script first — it checks all of the above for you:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows-diagnostics.ps1
```

See `WINDOWS_DIAGNOSTICS.md` for how to interpret its output. Fix every
FAIL before starting the items below.

**Test data to use everywhere below** (so results are comparable between
runs): the sentence `Hello Aurora, this is a Windows test 123.` and the
folder `%USERPROFILE%\Documents\AuroraTest`. Create it first:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\Documents\AuroraTest"
```

**Where evidence goes:** for every item, record in
`WINDOWS_ACCEPTANCE_CHECKLIST.md`: PASS/FAIL, the exact text or file the app
produced, and the log lines in
`%LOCALAPPDATA%\AuroraOperator\Logs\aurora-operator-<today>.log` and
`%LOCALAPPDATA%\AuroraOperator\Logs\audit.log`. Screenshots of the app
window go next to the checklist (same folder, `evidence\` subfolder).

**Known quirks to watch for** (from `WINDOWS_VERIFICATION_CHECKLIST.md`):
- Plan mode asks for approval once per step at review time, then the engine
  asks *again* at execution time for each sensitive step. This double
  prompting is intentional (no scoped plan-approval token yet) — it is not
  a failure.
- Windows 11 Notepad is tabbed and its Save dialog differs from classic
  Notepad. If the Save-dialog automation cannot drive it, the app falls
  back to a verified direct file write and says so in the result — record
  which path actually ran (item 8).

---

## Item 1 — App launch

**Steps**

1. Close all Aurora Operator windows.
2. Launch from the Start Menu: Start → Aurora Operator → Aurora Operator
   (or double-click the Desktop shortcut).
3. Wait up to 30 seconds for the main window.

**Expected result**

- The main window opens with no error dialog. The status bar shows either
  `Ready. Local model: qwen3:4b.` (or `qwen3:1.7b`) or, if Ollama is not
  running, `Ollama is not running. Start Ollama, or open Model Manager to
  install it.`
- Only one instance runs: launching a second time shows "Aurora Operator
  is already running." and exits.

**What counts as FAIL**

- Crash, unhandled-exception dialog, or a blank window.
- A second instance opens instead of the single-instance message.
- The app exits silently within 30 seconds.

**Evidence:** status-bar text; `%LOCALAPPDATA%\AuroraOperator\Logs\aurora-operator-<today>.log`
should contain `Aurora Operator starting.`

## Item 2 — Ollama detection

**Steps**

1. With the app running, confirm Ollama is running: in PowerShell,
   `Invoke-RestMethod http://localhost:11434/api/tags` returns a JSON list.
2. In the app, look at the Ollama status indicator and the Model Manager
   panel (model list).
3. Stop Ollama (`Stop-Process -Name ollama`), wait 10 seconds, and look at
   the status indicator again. Then start Ollama again (`ollama serve` in
   a terminal, or launch Ollama from the Start Menu) and wait 10 seconds.

**Expected result**

- With Ollama up: status indicator green / "Connected to Ollama
  (localhost:11434)", model list shows the pulled model(s).
- With Ollama stopped: status shows Ollama offline; the app does **not**
  crash and does not freeze.
- With Ollama back: status returns to connected without restarting the app.

**What counts as FAIL**

- Status says connected while `Invoke-RestMethod` fails (or vice versa).
- The app crashes, hangs, or needs a restart to notice Ollama is back.

**Evidence:** status text in each of the three states; log lines
`Ollama availability check failed` (if any) in the daily log.

## Item 3 — Model download

**Steps**

1. Pick a model that is **not** installed yet (check `ollama list` first).
   On an 8 GB+ machine use `qwen3:1.7b` if `qwen3:4b` is already there (or
   vice versa); on a 4–8 GB machine use `qwen3:0.6b`.
2. In the app, open Model Manager and download that model; watch the
   progress display.
3. When it finishes, run `ollama list` in PowerShell.

**Expected result**

- Progress advances (not stuck at 0%) and the download completes without
  an error dialog.
- `ollama list` shows the new model afterwards.
- If the download is interrupted (close the app mid-download, or unplug
  the network), the app reports the failure honestly instead of claiming
  the model is ready.

**What counts as FAIL**

- Progress never moves, or the app claims success while `ollama list`
  does not show the model.
- A failed download is reported as a success.

**Evidence:** `ollama list` output before/after; log lines `Model download
failed` (if any).

## Item 4 — Real AI inference

**Steps**

1. In the app, make sure the pulled model is selected in Model Manager.
2. Set mode to **Ask** and send: `What is the capital of France? Answer in one sentence.`
3. Set mode to **Ask** and send (Chinese): `用中文一句话介绍北京。`
4. Note the reply text and how long it took.

**Expected result**

- Each reply is a correct, on-topic answer in the language asked, produced
  by the local model (not a canned local summary). A reply that says
  "Ollama was not reachable" or "this is the local summary" means
  inference did **not** happen — see FAIL.
- First reply may take up to ~60 seconds on first load (model loading);
  later replies are faster.

**What counts as FAIL**

- Any reply that is the local fallback summary, an error, or empty.
- A reply in the wrong language when the model was asked in Chinese.
- No reply within 120 seconds.

**Evidence:** the exact reply text (copy it into the checklist); time taken
for the first reply.

## Item 5 — Notepad launch

**Steps**

1. Set mode to **Autonomous**.
2. Send: `Open Notepad and write Hello Aurora, this is a Windows test 123.`
3. Approve any approval prompt that appears.
4. Watch the screen: Notepad must visibly open.

**Expected result**

- A Notepad window opens on screen within 10 seconds of approval.
- The task report says the step succeeded.

**What counts as FAIL**

- No Notepad window appears within 10 seconds.
- The task reports success while no Notepad window opened.
- Notepad opens but the app reports failure with no useful error.

**Evidence:** screenshot of the open Notepad window; task Final Report text.

## Item 6 — Actual text entry

**Steps**

1. Continue from item 5 (Notepad open, or repeat item 5 first).
2. Confirm the exact sentence `Hello Aurora, this is a Windows test 123.`
   is visible in the Notepad window.

**Expected result**

- The full sentence is in Notepad, character for character, including the
  final period — typed by the app, not pasted by the tester.

**What counts as FAIL**

- Missing, truncated, garbled, or extra characters.
- The text is in the wrong window (e.g. typed into the Aurora chat box).
- The task reports success while the text is wrong.

**Evidence:** screenshot of the Notepad content.

## Item 7 — Text verification

**Steps**

1. Continue from item 6.
2. In the app, look at the step result for the typing step: it must say
   the read-back was verified (exact match).
3. Deliberately break it once: click into Notepad, change `123` to `999`,
   then ask the app (Autonomous): `Verify the text in Notepad matches Hello Aurora, this is a Windows test 123.`

**Expected result**

- Step 2: the result says read-back verified an exact match.
- Step 3: the app reports a **mismatch** (it must not claim the text
  matches when it does not).

**What counts as FAIL**

- Step 2 claims verification without actually reading the text back.
- Step 3 reports a match for the altered text, or reports success.

**Evidence:** the two step-result texts, copied into the checklist.

## Item 8 — File saving

**Steps**

1. With Notepad still open from items 5–7 (restore the text to
   `Hello Aurora, this is a Windows test 123.` first if you changed it),
   send (Autonomous): `Save the Notepad text to %USERPROFILE%\Documents\AuroraTest\hello.txt`
   (type the real path, e.g. `C:\Users\<you>\Documents\AuroraTest\hello.txt`).
2. Approve any prompt.
3. Open the file in PowerShell: `Get-Content "$env:USERPROFILE\Documents\AuroraTest\hello.txt" -Raw`
   and compare byte-for-byte: `(Get-FileHash "$env:USERPROFILE\Documents\AuroraTest\hello.txt").Hash`
   against the hash of the expected text written by hand.

**Expected result**

- The file exists at the requested path and its content is exactly
  `Hello Aurora, this is a Windows test 123.` (no extra line, no BOM, no
  truncation).
- The result says whether the save went through the Notepad Save dialog
  or the verified direct-write fallback — record which one it was.

**What counts as FAIL**

- File missing, wrong path, or content differs by even one character.
- The result claims a Save-dialog save when the fallback ran (or the
  reverse), or claims success while the file is wrong.

**Evidence:** `Get-Content` output; the two hashes; which save path ran.

## Item 9 — Approval and rejection

**Steps**

1. In `%USERPROFILE%\Documents\AuroraTest`, create a file to overwrite:
   `Set-Content "$env:USERPROFILE\Documents\AuroraTest\existing.txt" 'original'`.
2. Send (Autonomous): `Write Hello Aurora, this is a Windows test 123. to C:\Users\<you>\Documents\AuroraTest\existing.txt`
   (use your real path).
3. When the approval dialog appears, check that it shows the exact action
   and file path, then click **Deny**.
4. Confirm the file still contains `original`:
   `Get-Content "$env:USERPROFILE\Documents\AuroraTest\existing.txt" -Raw`
5. Send the same request again; this time click **Approve**.
6. Confirm the file now contains the new sentence.

**Expected result**

- An approval dialog appears **before** anything is written (overwriting
  an existing file is Sensitive and must be approved).
- After Deny: file unchanged, task reports the step was not executed.
- After Approve: file contains the new sentence.
- Both decisions appear in `%LOCALAPPDATA%\AuroraOperator\Logs\audit.log`.

**What counts as FAIL**

- No approval dialog appears and the file is overwritten anyway.
- After Deny the file changed, or after Approve it did not.
- Either decision is missing from `audit.log`.

**Evidence:** `audit.log` lines for both decisions; file contents after
each step.

## Item 10 — Task cancellation

**Steps**

1. Send a long task (Autonomous): `Organise all files in %USERPROFILE%\Documents\AuroraTest by extension`
   (first put 20+ mixed files in that folder so the task takes a while,
   or use a larger folder).
2. While it is running, click **Stop**.
3. Repeat the task, and this time click **Pause**, wait 10 seconds, check
   the task state shows Paused, then click **Resume** and let it finish.
4. Repeat once more, but this time start an approval (send the item 9
   overwrite request) and click **Stop while the approval dialog is open**.

**Expected result**

- Stop: task ends as Cancelled promptly (within ~5 seconds); no further
  files are moved after the click.
- Pause/Resume: state shows Paused, nothing moves while paused, work
  continues after Resume.
- Stop during approval: the pending action **never runs**; the file is
  unchanged.

**What counts as FAIL**

- Work continues after Stop, or the app freezes and never reaches
  Cancelled.
- Pause does not pause, or Resume does not resume.
- The pending approval's action runs after Stop.

**Evidence:** task states and timestamps from the app; file listing of
`AuroraTest` before/after; `audit.log` for the cancelled approval.

## Item 11 — Browser automation

**Steps**

1. Confirm Playwright Chromium is installed (diagnostics item 9).
2. Send (Autonomous): `Research the history of the Eiffel Tower and save a report`
3. Wait for the report. Open it (the app reports its path; default is
   `%TEMP%\aurora-research-report.md`).
4. Check the report contents.

**Expected result**

- A markdown report exists with real sourced findings (a source URL and
  several paragraphs about the Eiffel Tower).
- If a page shows a CAPTCHA, the report records it as Blocked and moves
  on — the app never tries to bypass it.

**What counts as FAIL**

- No report, an empty report, or invented content with no source URL.
- The app claims success while every query failed or was blocked.
- Any attempt to solve a CAPTCHA or log in to a site.

**Evidence:** the report file (copy it into `evidence\`); its source URLs.

## Item 12 — Memory persistence

**Steps**

1. Complete any Autonomous task (e.g. item 8) and note its entry in the
   **Recent Tasks** panel.
2. In the app, send a chat message: `Remember that my favourite colour is teal.`
   (or save any preference the app offers).
3. Close the app completely (File → Exit, or close the window and confirm
   no `AuroraOperator` process remains in Task Manager).
4. Reopen the app. Check Recent Tasks and the memory/conversation view.
5. Double-click the earlier task in Recent Tasks.

**Expected result**

- The earlier task is still listed after restart, with its Final Report
  intact.
- The remembered note/preference is still there after restart.
- Double-clicking reloads the task into the centre view.

**What counts as FAIL**

- Recent Tasks is empty after restart, or a task's report is lost.
- The remembered note is gone.
- The app crashes on restart while loading memory.

**Evidence:** screenshots of Recent Tasks before/after restart;
`%LOCALAPPDATA%\AuroraOperator\aurora.db` exists and its modified time
updates on close.

## Item 13 — Offline operation

**Steps**

1. With the app running and Ollama up, disconnect the network (unplug
   Ethernet / turn on flight mode).
2. Send (Ask): `What is 17 + 25?`
3. Send (Autonomous): `Open Notepad and write Hello Aurora, this is a Windows test 123.`
4. Send (Autonomous): `Research the history of the Eiffel Tower`
5. Reconnect the network.

**Expected result**

- Steps 2 and 3 work exactly as online (local model, local Notepad).
- Step 4 fails honestly with a clear "no network / research failed"
  message and no fabricated report.

**What counts as FAIL**

- Local tasks fail or fall back to the local summary while offline.
- The research step claims success offline or produces a fake report.
- The app crashes when the network drops.

**Evidence:** the three results, copied into the checklist; note the
time the network was down.

## Item 14 — App restart

**Steps**

1. With tasks in Recent Tasks and a model selected, close the app.
2. Confirm no `AuroraOperator` process is left in Task Manager.
3. Reopen the app from the Start Menu.
4. Check: model selection, Recent Tasks, theme setting, and Ollama status
   are as you left them.

**Expected result**

- The app starts cleanly, no "already running" error, no crash.
- Previous state (model, recent tasks, settings) is restored.

**What counts as FAIL**

- "Already running" error on a clean start, or a leftover process blocks
  the new start.
- Settings or recent tasks lost (see also item 12).

**Evidence:** Task Manager process list after close; state after reopen.

## Item 15 — Uninstallation

**Steps**

1. Close the app.
2. Open Settings → Apps → Installed apps → Aurora Operator → Uninstall
   (or run `%ProgramFiles%\Aurora Operator\uninstall.exe`).
3. When asked about the downloaded Ollama models, choose **No**
   (remove the models) on the first run of this item; see the note below
   for the second run.
4. After uninstall finishes, check:
   - `%ProgramFiles%\Aurora Operator` is gone.
   - Start Menu and Desktop shortcuts are gone.
   - The app no longer appears in Installed apps.
   - `%USERPROFILE%\.ollama\models` is gone (you chose "remove models").
   - Ollama itself is still installed (it is a shared tool).
   - `%LOCALAPPDATA%\AuroraOperator` (memory database and logs) is still
     there — uninstall deliberately leaves it.
5. Reinstall the app, then uninstall again choosing **Yes** (keep models)
   and confirm `%USERPROFILE%\.ollama\models` remains.

**Expected result**

- Uninstall completes with no error and removes the app, shortcuts, and
  registry entry.
- Model removal follows the choice made in the prompt, both ways.
- User data (`%LOCALAPPDATA%\AuroraOperator`) survives uninstall.

**What counts as FAIL**

- Uninstall errors, or leaves the program folder, shortcuts, or registry
  entry behind.
- Models are removed when "keep" was chosen, or kept when "remove" was
  chosen.
- User data is deleted.

**Evidence:** directory listings before/after each uninstall; the prompt
answer chosen each time.

---

## After the run

1. Fill in `WINDOWS_ACCEPTANCE_CHECKLIST.md` (every box ticked PASS/FAIL,
   with the evidence noted).
2. Copy the diagnostics report (`aurora-diagnostics-*.txt` from the
   Desktop) and any screenshots into `tests/windows/evidence/`.
3. For each FAIL, write: the exact step, what happened vs. expected, the
   error text, and whether it blocks packaging. A single failed Notepad
   Save-dialog step does not invalidate the Linux test runs — it narrows
   the fix to `WindowsAppControl.SaveNotepadFile` / `NotepadWorkflow`.

## Phase 8 additions (2026-11-21) — Windows procedures for the new work

These procedures test the Phase 8 changes. None has run anywhere
yet; each is BLOCKED until a Windows machine runs it. Record the
result in `WINDOWS_ACCEPTANCE_CHECKLIST.md` (Phase 8 section).

### P8-1. Trimmed publish launches
1. Copy `publish/win-x64-self-contained/` (270 MB) to the Windows
   machine. 2. Run `AuroraOperator.exe`. Expect: the window opens,
   no missing-DLL error, and the app's diagnostics show SQLite
   working (memory persists after restart). FAIL if any native DLL
   error appears — the flattened layout (`e_sqlite3.dll` at the
   publish root) is the suspect, not the trim itself.

### P8-2. Chromium on the trimmed build
1. In the app (or PowerShell), run `playwright install chromium`
   from the publish folder. 2. Ask the agent to research one topic
   and download one file. Expect: a sourced report and a non-zero
   downloaded file in Downloads. FAIL if Chromium cannot launch —
   that means the trim removed something Windows needs, and the
   trim must be reverted before release.

### P8-3. 32 GB / sub-8 GB model selection
1. On a 32 GB machine, run the installer or the app's Model Manager
   and note the selected model. Expect: qwen3:8b. 2. On a machine
   below 8 GB, expect qwen3:0.6b. 3. On a 32 GB machine with most
   RAM in use (< 10 GB free), expect the app to fall back to 4b or
   1.7b. FAIL if 8b is pulled on a machine that cannot hold it, or
   if 1.7b is pulled below 8 GB (the old contradiction).

### P8-4. Inno forced health-check failure
1. Build the Inno installer (`iscc InnoSetup.iss`). 2. Break the
   health check on purpose: after the files copy (or by editing a
   test copy of the script to point the model check at a model that
   does not exist), run the installer. Expect: an error dialog,
   setup CANCELS (no "completed successfully" page), and the
   installed app files are removed. FAIL if setup reports success.
   Do not ship the Inno installer until this passes.

### P8-5. Ollama retry observable
1. Start a chat request, then stop the Ollama service mid-request.
   Expect: the app reports a clean failure within seconds, never
   hangs, and does not retry forever. 2. Ask for a model that does
   not exist. Expect: an immediate failure (no retry delay).

### P8-6. Launch allowlist bypass attempts
Ask the agent, in separate tasks: "open https://example.com",
"run setup.bat", "launch chrome.exe". Expect: the URL and the
script are refused outright with a clear message; chrome.exe asks
for approval showing the exact target. FAIL if anything launches
without approval, or if a script executes.

### P8-7. Close-app guard
Ask the agent to close `explorer`, then `svchost`. Expect: both
refused outright. Then ask it to close Notepad with a document
open. Expect: an approval prompt; denying closes nothing;
approving closes only Notepad. FAIL if a protected process is
touched or if anything closes without approval.

### P8-8. DPAPI preferences + DB ACL
1. In the app, change a preference (theme or model). 2. Close and
   reopen the app: the preference persists. 3. Open
   `%LOCALAPPDATA%\AuroraOperator\aurora.db` in a hex viewer and
   search for the preference value: it must NOT be readable
   plaintext. 4. Run
   `icacls %LOCALAPPDATA%\AuroraOperator\aurora.db` and confirm
   only the current user has access. FAIL if the value is plaintext
   or another user has access.

### P8-9. Ollama loopback binding (real network test)
1. With Ollama running, run `netstat -ano | findstr 11434`:
   expect a listener on 127.0.0.1:11434 only. 2. From another
   machine on the same LAN, try to reach
   `http://<windows-ip>:11434/api/tags`: expect a connection
   failure. FAIL if Ollama answers on any non-loopback address.

### P8-10. `dotnet test` executes real tests
Run `dotnet test tests/AuroraOperator.Tests -c Release` and record
the exact pass/fail count. Expect a non-zero count (the Linux
sandbox runs 0 only because its proxy corrupts the testhost
channel). FAIL if the count is 0 or if real tests fail.
