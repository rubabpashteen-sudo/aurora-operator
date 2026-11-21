# Aurora Operator 0.3.0 RC.2

RC.2 rebuilds the desktop around a glass workspace and adds approval-gated built-in Windows launches.

- The new WPF shell uses Windows 11 Desktop Acrylic where available and an opaque fallback on older systems. The workspace, messages, composer, task inspector, model selector and approval dialog share a new theme.
- Explicit requests such as “open my computer” route directly to the built-in This PC launch with exact approval. File Explorer, Calculator, and Notepad also have fixed launch targets.
- Startup reconciles the selected model with installed Ollama models. Model runtime errors are shown separately from missing-model errors.
- Ask streams assistant messages. Plan previews one fixed plan and runs it only when requested. Agent can inspect a folder, use tool results for follow-up planning, and pause for approval.
- File operations include top-level folder listing, bounded duplicate scans, small UTF-8 reads, single-folder creation, no-overwrite file moves, verified new text creation, and opening a saved text file in Windows Notepad.
- The desktop has collapsible navigation and details, workspace selection, keyboard shortcuts, copyable messages, model controls, status cards, conversation search and reopening.
- Exact file changes require approval. The planner rejects unknown tools, unsupported arguments, path traversal, Windows device paths and reparse points. It receives a limited number of planning passes and tool calls. Approvals cannot be supplied by model output.
- The SQLite migration retains existing tasks and adds conversation grouping and workspace association. Interrupted actions remain history entries and are never replayed automatically.
- The release build produces a per-user NSIS setup, self-contained portable ZIP, SHA-256 hashes and official xUnit test results. The Windows workflow tests before packaging.

This RC exposes only the workspace registry in Plan and Agent. Browser research, coding execution and document conversion remain in the legacy Core implementation. They are outside the new desktop workflow. Notepad integration passes the saved text file to Windows and does not verify the editor window or use UI Automation to enter the letter. Message rendering is selectable plain text. Large batch organization, full project management, plan parameter editing, rich Markdown and syntax highlighting are unfinished.

The packages are unsigned. The setup offers the official Ollama download page when needed. Dependency installation and model download are separate explicit actions. This avoids replacing the user's installed Ollama version or model store during setup.
