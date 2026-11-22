# Install Aurora Operator 0.3.0 RC.3

1. Download the Windows x64 setup and `SHA256SUMS.txt` from the same artifact bundle. Verify the setup with `Get-FileHash -Algorithm SHA256 .\AuroraOperator-Setup-0.3.0-rc.3-x64.exe`.
2. Run the unsigned setup on 64-bit Windows 10 or 11. The setup installs per user and adds a Start menu entry. The .NET desktop runtime is included.
3. Start Ollama. If it is missing, use the setup's official download link or install it from Ollama's Windows download page.
4. Open Aurora Operator RC. Use **Model Manager** to choose an installed model or approve a download. Small models are suitable for light chat and may produce invalid plans. Use a model that fits available memory and verify that it answers an Ask prompt.
5. Launch **Aurora Operator RC** from Start. Its title shows **RC 3**. The old **Aurora Operator** shortcut may still launch version 0.2.0.
6. Choose **Workspace** and select a small folder. Try **Agent** with `list workspace`, then choose **Plan** to review a proposed file creation. Select **Run plan** and check the file-specific approval before approving it.

The portable ZIP runs from its extracted directory using `AuroraOperator.exe`. Both packages use `%LOCALAPPDATA%\AuroraOperator` by default. To keep a portable test profile separate, launch `AuroraOperator.exe --data-directory "C:\path\to\test-profile"` with an absolute writable folder.

Conversation text, task history and workspace paths are stored in plaintext SQLite. Preferences follow the existing Windows DPAPI path. **Clear conversations and tasks** removes saved history entries from the database. It is a logical delete and does not promise forensic erasure.

Uninstall from Windows Installed Apps or the RC Start menu shortcut. Setup removes the shipped app files and its integration. It preserves local data, Ollama models and other files added to the installation folder. Existing Aurora Operator 0.2.0 program files are separate. The default profile is shared, and the RC migrates its history schema in place.
