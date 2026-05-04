# Claude Code Plugin — MSI Installer Integration

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a user runs the SaddleRAG Windows MSI installer, it optionally registers the Claude Code plugin in their `~/.claude` profile so SaddleRAG tools are available in Claude Code without any manual setup.

**Architecture:** Plugin files (`plugin/`) are always copied to `[MCPFOLDER]\plugin\` as part of the standard install payload. A new installer dialog with a checked-by-default checkbox lets the user opt in to automatic registration. If they opt in, a deferred, impersonated PowerShell custom action copies the plugin files to `%USERPROFILE%\.claude\plugins\saddlerag\` and patches `%USERPROFILE%\.claude\settings.json` to add the `mcpServers.saddlerag` entry. A matching uninstall action removes both. Registration works whether or not Claude Code is currently installed — the files and settings will be picked up when it is.

**Tech Stack:** WiX 4 / WiX Toolset 5.0.2, PowerShell 5+, GitHub Actions (windows-latest), `.claude/settings.json` user-level Claude Code settings

---

## File Map

| File | Change |
|---|---|
| `.github/workflows/build.yml` | Add `-d PluginSourceDir=` to `wix build` step |
| `SaddleRAG.Installer/Package.wxs` | All WiX changes: directory, component group, property, CAs, dialog, navigation |

---

## Task 1: Add PluginSourceDir to CI build

**Files:**
- Modify: `.github/workflows/build.yml:79`

The `wix build` step needs a new `-d PluginSourceDir=` argument pointing at the `plugin/` directory in the checked-out repo. The path uses `${{ github.workspace }}` (same pattern as `PublishDir`).

- [ ] **Step 1: Edit build.yml — extend the wix build command**

Current line 79:
```yaml
        run: wix build SaddleRAG.Installer/Package.wxs -d PublishDir=${{ github.workspace }}/artifacts/${{ env.PackageVersion }}/publish -d Version=${{ env.MsiVersion }} -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext -arch x64 -o ./artifacts/${{ env.PackageVersion }}/SaddleRAG.Mcp.msi
```

Replace with (add `-d PluginSourceDir=...` after `-d Version=...`):
```yaml
        run: wix build SaddleRAG.Installer/Package.wxs -d PublishDir=${{ github.workspace }}/artifacts/${{ env.PackageVersion }}/publish -d Version=${{ env.MsiVersion }} -d PluginSourceDir=${{ github.workspace }}/plugin -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext -arch x64 -o ./artifacts/${{ env.PackageVersion }}/SaddleRAG.Mcp.msi
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -F msg.txt
```
`msg.txt`:
```
Pass PluginSourceDir to wix build
```

---

## Task 2: Add plugin files to installer payload (Package.wxs)

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`

Plugin files (`plugin/`) are always installed to `[MCPFOLDER]\plugin\`. They're small (~10 KB total) and serve as the manual-install source as well. A new `PLUGINFOLDER` directory child of `MCPFOLDER`, a `PluginFiles` component group using WiX 4's `<Files>` glob, and a `ComponentGroupRef` in the `Main` feature wire this up.

- [ ] **Step 1: Add PLUGINFOLDER directory inside MCPFOLDER**

In `Package.wxs`, find:
```xml
        <!-- Directories -->
        <StandardDirectory Id="ProgramFiles64Folder">
            <Directory Id="INSTALLFOLDER" Name="SaddleRAG">
                <Directory Id="MCPFOLDER" Name="SaddleRAG.Mcp" />
            </Directory>
        </StandardDirectory>
```

Replace with:
```xml
        <!-- Directories -->
        <StandardDirectory Id="ProgramFiles64Folder">
            <Directory Id="INSTALLFOLDER" Name="SaddleRAG">
                <Directory Id="MCPFOLDER" Name="SaddleRAG.Mcp">
                    <Directory Id="PLUGINFOLDER" Name="plugin" />
                </Directory>
            </Directory>
        </StandardDirectory>
```

- [ ] **Step 2: Add PluginFiles ComponentGroup after PublishOutput**

After the existing `<ComponentGroup Id="PublishOutput" ...>` block, add:
```xml
        <!-- Plugin files (always installed; registration is opt-in via dialog) -->
        <ComponentGroup Id="PluginFiles" Directory="PLUGINFOLDER">
            <Files Include="$(var.PluginSourceDir)\**" />
        </ComponentGroup>
```

- [ ] **Step 3: Reference PluginFiles in the Main feature**

Find:
```xml
        <Feature Id="Main" Title="SaddleRAG MCP Server" Level="1">
            <ComponentGroupRef Id="PublishOutput" />
            <ComponentRef Id="ServiceComponent" />
        </Feature>
```

Replace with:
```xml
        <Feature Id="Main" Title="SaddleRAG MCP Server" Level="1">
            <ComponentGroupRef Id="PublishOutput" />
            <ComponentGroupRef Id="PluginFiles" />
            <ComponentRef Id="ServiceComponent" />
        </Feature>
```

- [ ] **Step 4: Verify the WiX build compiles (local)**

Run from the repo root (substitute real publish dir — use a prior build or `dotnet publish` first):
```powershell
wix build SaddleRAG.Installer/Package.wxs `
  -d PublishDir=.\artifacts\0.0.0\publish `
  -d Version=0.0.0 `
  -d PluginSourceDir=.\plugin `
  -ext WixToolset.Util.wixext `
  -ext WixToolset.UI.wixext `
  -arch x64 `
  -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

Expected: build succeeds, `SaddleRAG.Mcp.msi` created. If `artifacts\0.0.0\publish` doesn't exist yet, run `dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\0.0.0\publish` first.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```
`msg.txt`:
```
Add plugin files to MSI installer payload
```

---

## Task 3: Add REGISTER_CLAUDE_PLUGIN property and registration custom actions

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`

Three additions: (a) a `REGISTER_CLAUDE_PLUGIN` property defaulting to `"1"` (checked), (b) a deferred impersonated `RegisterClaudePlugin` custom action that copies plugin files and patches `settings.json`, and (c) a deferred impersonated `UnregisterClaudePlugin` custom action that reverses both on uninstall.

The CAs use `WixQuietExec` with an inline PowerShell `-Command`. Both are `Impersonate="yes"` so `$env:USERPROFILE` resolves to the installing user's profile, not SYSTEM's. `[MCPFOLDER]` in the command string is expanded by MSI's `SetProperty` (which runs in the elevated execute sequence before impersonation) — the already-expanded path string is what WixQuietExec receives.

- [ ] **Step 1: Add REGISTER_CLAUDE_PLUGIN property**

Find the block of `<Property>` declarations (after `<Property Id="OLLAMASTATUS" ...>`). Add:
```xml
        <Property Id="REGISTER_CLAUDE_PLUGIN" Value="1" />
        <Property Id="CLAUDEPLUGINSTATUS" Secure="yes" />
```

- [ ] **Step 2: Add SetProperty + CustomAction for RegisterClaudePlugin**

After the `PatchAppSettings` custom action pair, add:

```xml
        <!-- Register Claude Code plugin: copy files + patch ~/.claude/settings.json -->
        <!-- Impersonate="yes": runs as the installing user so $env:USERPROFILE resolves correctly. -->
        <!-- [MCPFOLDER] is expanded at SetProperty time (elevated context) before impersonation. -->
        <SetProperty Id="RegisterClaudePlugin"
                     Before="RegisterClaudePlugin"
                     Sequence="execute"
                     Value="&quot;powershell.exe&quot; -NoProfile -ExecutionPolicy Bypass -Command &quot;$src = Join-Path '[MCPFOLDER]' 'plugin'; $dst = Join-Path $env:USERPROFILE '.claude\plugins\saddlerag'; if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }; New-Item -ItemType Directory -Path $dst -Force | Out-Null; Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force; $sf = Join-Path $env:USERPROFILE '.claude\settings.json'; $sd = Split-Path $sf; if (-not (Test-Path $sd)) { New-Item -ItemType Directory -Path $sd -Force | Out-Null }; if (Test-Path $sf) { $s = Get-Content $sf -Raw | ConvertFrom-Json } else { $s = [PSCustomObject]@{} }; if (-not $s.PSObject.Properties['mcpServers']) { $s | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([PSCustomObject]@{}) }; $e = [PSCustomObject]@{ type = 'http'; url = 'http://localhost:6100/mcp'; timeout = 60 }; $s.mcpServers | Add-Member -MemberType NoteProperty -Name 'saddlerag' -Value $e -Force; $s | ConvertTo-Json -Depth 10 | Set-Content $sf -Encoding UTF8&quot;" />

        <CustomAction Id="RegisterClaudePlugin"
                      BinaryRef="Wix4UtilCA_X86"
                      DllEntry="WixQuietExec"
                      Execute="deferred"
                      Return="ignore"
                      Impersonate="yes" />
```

- [ ] **Step 3: Add SetProperty + CustomAction for UnregisterClaudePlugin**

Immediately after the RegisterClaudePlugin pair, add:

```xml
        <!-- Unregister Claude Code plugin on uninstall: remove files + remove mcpServers entry -->
        <SetProperty Id="UnregisterClaudePlugin"
                     Before="UnregisterClaudePlugin"
                     Sequence="execute"
                     Value="&quot;powershell.exe&quot; -NoProfile -ExecutionPolicy Bypass -Command &quot;$dst = Join-Path $env:USERPROFILE '.claude\plugins\saddlerag'; if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }; $sf = Join-Path $env:USERPROFILE '.claude\settings.json'; if (Test-Path $sf) { $s = Get-Content $sf -Raw | ConvertFrom-Json; if ($s.PSObject.Properties['mcpServers'] -and $s.mcpServers.PSObject.Properties['saddlerag']) { $s.mcpServers.PSObject.Properties.Remove('saddlerag'); $s | ConvertTo-Json -Depth 10 | Set-Content $sf -Encoding UTF8 } }&quot;" />

        <CustomAction Id="UnregisterClaudePlugin"
                      BinaryRef="Wix4UtilCA_X86"
                      DllEntry="WixQuietExec"
                      Execute="deferred"
                      Return="ignore"
                      Impersonate="yes" />
```

- [ ] **Step 4: Add both CAs to InstallExecuteSequence**

Find:
```xml
        <InstallExecuteSequence>
            <Custom Action="PatchAppSettings" After="InstallFiles" Condition="NOT Installed OR REINSTALL" />
            <Custom Action="PrewarmService" Before="StartAndMonitorService" Condition="NOT Installed OR REINSTALL" />
            <Custom Action="StartAndMonitorService" Before="StartServices" Condition="NOT Installed OR REINSTALL" />
        </InstallExecuteSequence>
```

Replace with:
```xml
        <InstallExecuteSequence>
            <Custom Action="PatchAppSettings" After="InstallFiles" Condition="NOT Installed OR REINSTALL" />
            <Custom Action="RegisterClaudePlugin" After="PatchAppSettings" Condition="(NOT Installed OR REINSTALL) AND REGISTER_CLAUDE_PLUGIN = &quot;1&quot;" />
            <Custom Action="UnregisterClaudePlugin" Before="RemoveFiles" Condition="REMOVE = &quot;ALL&quot;" />
            <Custom Action="PrewarmService" Before="StartAndMonitorService" Condition="NOT Installed OR REINSTALL" />
            <Custom Action="StartAndMonitorService" Before="StartServices" Condition="NOT Installed OR REINSTALL" />
        </InstallExecuteSequence>
```

- [ ] **Step 5: Verify build**

```powershell
wix build SaddleRAG.Installer/Package.wxs `
  -d PublishDir=.\artifacts\0.0.0\publish `
  -d Version=0.0.0 `
  -d PluginSourceDir=.\plugin `
  -ext WixToolset.Util.wixext `
  -ext WixToolset.UI.wixext `
  -arch x64 `
  -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```
`msg.txt`:
```
Add Claude Code plugin registration custom actions
```

---

## Task 4: Add ClaudePluginDlg dialog and wire navigation

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`

A new 370×270 dialog `ClaudePluginDlg` sits between `OllamaDlg` and `VerifyReadyDlg`. It has a checkbox that toggles `REGISTER_CLAUDE_PLUGIN`. Two navigation updates: `OllamaDlg`'s Next goes to `ClaudePluginDlg` instead of `VerifyReadyDlg`, and the `VerifyReadyDlg` Back publish changes from `OllamaDlg` to `ClaudePluginDlg`.

- [ ] **Step 1: Add ClaudePluginDlg dialog**

Inside the `<UI>` block, after the closing `</Dialog>` of `OllamaDlg` and before the wire-up `<Publish>` elements, add:

```xml
            <!-- Claude Code plugin registration dialog -->
            <Dialog Id="ClaudePluginDlg" Width="370" Height="270" Title="Claude Code Integration">
                <Control Id="BannerBitmap" Type="Bitmap" X="0" Y="0" Width="370" Height="44" TabSkip="no" Text="WixUI_Bmp_Banner" />
                <Control Id="BannerLine" Type="Line" X="0" Y="44" Width="370" Height="0" />

                <Control Id="Title" Type="Text" X="20" Y="10" Width="290" Height="14"
                         Transparent="yes" NoPrefix="yes"
                         Text="{\WixUI_Font_Title}Claude Code Integration" />

                <Control Id="Description" Type="Text" X="25" Y="55" Width="320" Height="50"
                         Transparent="yes" NoPrefix="yes"
                         Text="SaddleRAG can register itself as a Claude Code plugin so its documentation tools are available in every Claude Code session automatically." />

                <Control Id="ClaudePluginCheck" Type="CheckBox" X="25" Y="115" Width="320" Height="17"
                         Property="REGISTER_CLAUDE_PLUGIN" CheckBoxValue="1"
                         Text="Register as a Claude Code plugin (recommended)" />

                <Control Id="InfoText" Type="Text" X="25" Y="140" Width="320" Height="55"
                         Transparent="yes" NoPrefix="yes"
                         Text="This adds a plugin entry to %USERPROFILE%\.claude\settings.json and copies the plugin files to %USERPROFILE%\.claude\plugins\saddlerag\. It works whether or not Claude Code is currently installed. Uninstalling SaddleRAG will remove the registration." />

                <Control Id="BottomLine" Type="Line" X="0" Y="224" Width="370" Height="0" />
                <Control Id="Back" Type="PushButton" X="180" Y="243" Width="56" Height="17" Text="&amp;Back">
                    <Publish Event="NewDialog" Value="OllamaDlg" />
                </Control>
                <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="&amp;Next">
                    <Publish Event="NewDialog" Value="VerifyReadyDlg" />
                </Control>
                <Control Id="Cancel" Type="PushButton" X="304" Y="243" Width="56" Height="17" Cancel="yes" Text="Cancel">
                    <Publish Event="SpawnDialog" Value="CancelDlg" />
                </Control>
            </Dialog>
```

- [ ] **Step 2: Update OllamaDlg Next button to go to ClaudePluginDlg**

Find in OllamaDlg:
```xml
                <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="&amp;Next">
                    <Publish Event="NewDialog" Value="VerifyReadyDlg" />
                </Control>
```

Replace with:
```xml
                <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="&amp;Next">
                    <Publish Event="NewDialog" Value="ClaudePluginDlg" />
                </Control>
```

- [ ] **Step 3: Update VerifyReadyDlg Back wire to point at ClaudePluginDlg**

Find at the bottom of the `<UI>` block:
```xml
            <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="OllamaDlg" Order="4" />
```

Replace with:
```xml
            <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="ClaudePluginDlg" Order="4" />
```

- [ ] **Step 4: Verify build**

```powershell
wix build SaddleRAG.Installer/Package.wxs `
  -d PublishDir=.\artifacts\0.0.0\publish `
  -d Version=0.0.0 `
  -d PluginSourceDir=.\plugin `
  -ext WixToolset.Util.wixext `
  -ext WixToolset.UI.wixext `
  -arch x64 `
  -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

Expected: build succeeds. Open the MSI and click through the dialogs — the new "Claude Code Integration" dialog should appear between Ollama and the final Verify Ready screen.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```
`msg.txt`:
```
Add Claude Code plugin registration dialog to installer
```

---

## Task 5: Manual end-to-end test

No automated test harness exists for MSI behavior. Verify by running the MSI on a dev machine (or a VM).

**Prerequisites:** MongoDB running on 27017, Ollama running on 11434, a built `SaddleRAG.Mcp.msi` from Task 4.

- [ ] **Install — happy path with checkbox checked**

1. Run `SaddleRAG.Mcp.msi` as admin. Click through the dialogs. On the "Claude Code Integration" dialog, confirm the checkbox is checked. Complete the install.
2. Verify plugin files:
   ```powershell
   Get-ChildItem "$env:USERPROFILE\.claude\plugins\saddlerag" -Recurse
   ```
   Expected files: `.mcp.json`, `README.md`, `.claude-plugin\plugin.json`, `skills\saddlerag-first\SKILL.md`
3. Verify settings.json:
   ```powershell
   Get-Content "$env:USERPROFILE\.claude\settings.json" | ConvertFrom-Json | Select-Object -ExpandProperty mcpServers
   ```
   Expected: a `saddlerag` key with `type="http"`, `url="http://localhost:6100/mcp"`, `timeout=60`.

- [ ] **Install — checkbox unchecked**

1. Uninstall (via Programs and Features or `msiexec /x SaddleRAG.Mcp.msi`).
2. Re-run the installer. On the "Claude Code Integration" dialog, uncheck the checkbox.
3. Complete the install.
4. Verify plugin files do NOT exist in `%USERPROFILE%\.claude\plugins\saddlerag\`.
5. Verify `%USERPROFILE%\.claude\settings.json` does NOT contain a `saddlerag` mcpServers key (or the file is unchanged if it didn't have one).

- [ ] **Uninstall removes registration**

1. Starting from a state where the plugin is registered (from the first test above).
2. Uninstall via `msiexec /x SaddleRAG.Mcp.msi` or Programs and Features.
3. Verify plugin directory removed:
   ```powershell
   Test-Path "$env:USERPROFILE\.claude\plugins\saddlerag"
   ```
   Expected: `False`
4. Verify `settings.json` no longer contains the `saddlerag` key:
   ```powershell
   (Get-Content "$env:USERPROFILE\.claude\settings.json" | ConvertFrom-Json).mcpServers
   ```
   Expected: no `saddlerag` property (other keys should be untouched).

- [ ] **Claude Code picks up the registration (if installed)**

1. Start Claude Code (`claude`).
2. Ask: "Use list_libraries to show what documentation is indexed."
3. Expected: Claude Code calls `mcp__saddlerag__list_libraries` without any manual `.mcp.json` configuration. Returns an empty list (or populated, if you've indexed anything).

- [ ] **Commit if any fixes were made**

If manual testing uncovered issues and fixes were made, commit them:
```bash
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```
`msg.txt` should describe what was fixed.
