# AI Clients Integration — Design

**Date:** 2026-05-10
**Branch:** claude/ai-clients-integration

## Problem

A SaddleRAG user today has to discover and wire SaddleRAG into every AI tool they use, by hand. The current state is incoherent:

- The MSI ships [SaddleRAG.Installer/RegisterClaudePlugin.ps1](../../../SaddleRAG.Installer/RegisterClaudePlugin.ps1), which writes Claude Code's MCP entry to `~/.claude/settings.json` — **the wrong file**. Claude Code's user MCP config lives at `~/.claude.json` (flat file), not under `~/.claude/`. Every install since this script landed has silently no-op'd for Claude Code while appearing to succeed.
- The same script writes Claude Desktop correctly (because Desktop's config really does live where the script targets). So Desktop has worked; CC has not, masked by the Desktop half succeeding.
- The script also drops files into `~/.claude/plugins/saddlerag\` thinking that's how a Claude Code plugin gets installed. It isn't — Claude Code's plugin loader only sees plugins that are also listed in `~/.claude/plugins/installed_plugins.json`. Files alone register nothing.
- The `saddlerag-first` skill *has* been used (8 times historically per `~/.claude.json`'s `skillUsage`), but only because the user separately ran `claude --plugin-dir <repo>/plugin` at some point, which created an `installed_plugins.json` entry. That entry now points at a missing directory — dangling — so the skill stopped firing on subsequent installs.
- The repo also ships a project-scoped `.mcp.json` and a `plugin/` folder. Both are duplicate sources of truth that compete with whatever per-user wiring the MSI does. The plugin's own README warns that this collision causes double-registration.
- VSCode (Copilot Chat MCP) and the GitHub Copilot CLI get nothing from the MSI today. The user has to wire them up manually if they want SaddleRAG there.

The user-facing symptom: SaddleRAG works in Claude Desktop, sort-of works in Claude Code in this one repo (because of the project `.mcp.json`), is invisible in Claude Code in any other directory, is invisible in VSCode's native MCP, and is invisible in Copilot CLI. The skill that was the whole reason for the plugin has been silently dead since the install dir got cleared.

## Goals

1. The Windows MSI installer wires SaddleRAG into every supported per-user AI tool automatically — Claude Code (terminal + VSCode extension share config), Claude Desktop, VSCode native MCP, GitHub Copilot CLI.
2. Registration is **per-user** (current installing user's profile), idempotent, surgical (does not stomp on other MCP servers or unrelated config), and reversible by the same MSI's uninstall.
3. Registration logic lives in C# (`SaddleRAG.ClientIntegration`) and is exercised by automated tests, including a round-trip "register then unregister leaves the file byte-identical to its original state" test.
4. The MSI custom action becomes a one-line shell-out to `SaddleRAG.Cli.exe register-clients ...` — no inline PowerShell in WiX, no PS1 scripts in the installer payload.
5. Repair (`msiexec /fa`, Add/Remove Programs → Repair) and major upgrade re-execute the registration, restoring the canonical config without any per-user fiddling.
6. The user can also re-run registration manually via `SaddleRAG.Cli register-clients` — useful if they install Claude Desktop after the MSI ran, or want to recover from manual config edits without uninstalling.
7. The repo's project-scoped `.mcp.json` and `plugin/` folder go away — the global install becomes the single source of truth.

## Non-Goals

- Multi-user provisioning. The MSI runs elevated but only writes to the installing user's profile. Other users on the same Windows machine would re-run the registration themselves (or run the MSI per-user once `SaddleRAG.Cli` is on PATH).
- Pre-install scrub of legacy turds (the wrong-file Claude Code entry from the old MSI scheme, the dangling `installed_plugins.json` entry, the legacy `~/.claude/plugins/saddlerag\` directory). The user will hand-clean these once on their machine after the new design lands; future users won't have them. See "One-time hand cleanup" below.
- A reactive watcher in the SaddleRAG service that back-fills new AI tool installs in the background. Out of scope. Manual `SaddleRAG.Cli register-clients` covers the after-the-fact case.
- Touching arbitrary git repositories to remove project `.mcp.json` files the user might have. The MSI only writes to per-user config locations.
- Cross-platform (macOS / Linux) installer integration. The MSI is Windows-only; macOS/Linux users register via the CLI manually for now.

## Scope

The four target tools, their per-user config files, and what each writer does:

| Writer | Config file (under `%USERPROFILE%`) | What we set | Skill delivery |
|---|---|---|---|
| `ClaudeCodeWriter` | `\.claude.json` | `mcpServers.saddlerag = { type: "http", url: "http://localhost:6100/mcp", timeout: 300 }`; merge SaddleRAG read-only tools into `permissions.allow[]` | Drop `\.claude\skills\saddlerag-first\SKILL.md` |
| `ClaudeDesktopWriter` | `\AppData\Roaming\Claude\claude_desktop_config.json` | `mcpServers.saddlerag = { command: "npx", args: ["-y", "mcp-remote@latest", "http://localhost:6100/mcp", "--allow-http"] }` | n/a (Desktop has no skill concept) |
| `VsCodeMcpWriter` | `\AppData\Roaming\Code\User\mcp.json` | `servers.saddlerag = { type: "http", url: "http://localhost:6100/mcp" }` | n/a |
| `CopilotCliWriter` | TBD — verified during implementation | MCP server entry (and skill drop if supported) | TBD |

**Copilot CLI is a known unknown.** The exact config path and schema for the GitHub Copilot CLI on Windows is not yet confirmed. The implementation plan starts with a spike to verify; the writer ships behind a feature flag (default off) until the spike confirms. Worst case: the writer is documented as "manual setup required for now," and the dialog checkbox for it is greyed out with a tooltip.

## Architecture

### Module layout

A new project, `SaddleRAG.ClientIntegration`, single-purpose, no DB / Ingestion dependencies (so it loads fast inside the MSI custom action).

```
SaddleRAG.ClientIntegration/
    IClientWriter.cs
    ClientRegistrar.cs
    Models/
        SaddleRagEndpoint.cs
        RegisterResult.cs
        UnregisterResult.cs
        StatusResult.cs
    Writers/
        ClaudeCodeWriter.cs
        ClaudeDesktopWriter.cs
        VsCodeMcpWriter.cs
        CopilotCliWriter.cs
    Resources/
        saddlerag-first.md
SaddleRAG.Cli/
    (existing) — three new subcommands: register-clients, unregister-clients, status
SaddleRAG.Tests/ClientIntegration/
    ClaudeCodeWriterTests.cs
    ClaudeDesktopWriterTests.cs
    VsCodeMcpWriterTests.cs
    CopilotCliWriterTests.cs
    ClientRegistrarTests.cs
    RoundTripTests.cs
    Fixtures/
        claude-code/
        claude-desktop/
        vscode-mcp/
        copilot-cli/
```

### `IClientWriter` contract

```csharp
public interface IClientWriter
{
    string ClientName { get; }                              // "claude-code", etc.
    Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct);
    Task<UnregisterResult> UnregisterAsync(CancellationToken ct);
    Task<StatusResult> GetStatusAsync(CancellationToken ct);
}
```

Each writer takes its config-file path via constructor injection (not `Environment.GetFolderPath`), so tests point it at a temp dir.

### Behavioral rules every writer follows

- **Register:** read existing config → if file missing, start from empty object → ensure container (`mcpServers` or tool-specific) exists → set `saddlerag` key to canonical value (idempotent overwrite) → write file. **Never touch other servers, never touch unrelated keys.**
- **Unregister:** read existing config → if file or key missing, no-op success → otherwise remove only the `saddlerag` key → if `mcpServers` becomes empty, leave it as `{}` (do not tidy further) → write file. Per-tool skill files removed if applicable.
- **Skill files** (Claude Code, Copilot CLI): write content from the embedded `Resources/saddlerag-first.md`; on register, overwrite any existing file at the same path (and log a warning if it differed from canonical content); on unregister, delete the specific file/folder, leave sibling skills alone.
- **File encoding:** UTF-8 **without BOM** (matches the bug fix from the existing PS1 — Claude Desktop's parser rejects BOM).
- **Atomicity:** write to a temp file in the same directory and `File.Move` over the original to avoid half-written corruption if the process is killed mid-write.

### `ClientRegistrar` orchestration

- Iterates the four writers in a stable order
- Per-writer failure does not stop the others — collected into a `RegistrarResult`
- Exit code from `register-clients` / `unregister-clients`: `0` if all selected writers succeeded (or were clean no-ops for unregister), `2` if at least one failed, `1` only on invocation errors (bad flags)

### CLI surface

```
SaddleRAG.Cli register-clients   [--claude-code=0|1] [--claude-desktop=0|1] [--vscode-mcp=0|1] [--copilot-cli=0|1] [--quiet] [--log-file <path>]
SaddleRAG.Cli unregister-clients [--claude-code=0|1] [--claude-desktop=0|1] [--vscode-mcp=0|1] [--copilot-cli=0|1] [--quiet] [--log-file <path>]
SaddleRAG.Cli status             [--json]
```

Per-tool flags default to `1` (selected). The MSI passes whatever the user's checkboxes were at install time. `unregister-clients` from the MSI passes nothing (defaults to all) — uninstall does not honor the original install-time selection because we don't know what the user wants left behind, and removing entries we didn't write is a no-op anyway.

`status` is read-only diagnostic. Reports per tool: config file path, found?, `saddlerag` entry present?, endpoint matches canonical?, skill file present (where applicable)?

### MSI integration

**Dialog: rename `ClaudePluginDlg` → `AiClientsDlg`, four checkboxes pre-checked**

```
+- AI Tools Integration ----------------------------------+
|  SaddleRAG can register itself with these AI tools so   |
|  its tools and skill are available automatically in     |
|  every session. All recommended.                        |
|                                                         |
|  [x] Claude Code (terminal + VSCode extension)          |
|  [x] Claude Desktop                                     |
|  [x] VSCode (Copilot Chat MCP)                          |
|  [x] GitHub Copilot CLI                                 |
|                                                         |
|  Touches per-user config files only:                    |
|    %USERPROFILE%\.claude.json                           |
|    %APPDATA%\Claude\claude_desktop_config.json          |
|    %APPDATA%\Code\User\mcp.json                         |
|    (Copilot CLI config — verified during install)       |
+---------------------------------------------------------+
```

**Properties (public, secure, default `"1"`):**

```xml
<Property Id="REGISTER_CLAUDE_CODE"    Value="1" Secure="yes" />
<Property Id="REGISTER_CLAUDE_DESKTOP" Value="1" Secure="yes" />
<Property Id="REGISTER_VSCODE_MCP"     Value="1" Secure="yes" />
<Property Id="REGISTER_COPILOT_CLI"    Value="1" Secure="yes" />
```

Each binds to its checkbox. Power-user override: `msiexec /i SaddleRAG.Mcp.msi REGISTER_COPILOT_CLI=0`.

**Custom actions: replace the two PS1-script CAs with `WixQuietExec` calls to `SaddleRAG.Cli.exe`.**

```xml
<SetProperty Id="RegisterAiClients"
             Before="RegisterAiClients"
             Sequence="execute"
             Value="&quot;[INSTALLFOLDER]SaddleRAG.Cli.exe&quot; register-clients --claude-code=[REGISTER_CLAUDE_CODE] --claude-desktop=[REGISTER_CLAUDE_DESKTOP] --vscode-mcp=[REGISTER_VSCODE_MCP] --copilot-cli=[REGISTER_COPILOT_CLI] --quiet --log-file &quot;%TEMP%\SaddleRAG-register.log&quot;" />

<CustomAction Id="RegisterAiClients"
              BinaryRef="Wix4UtilCA_X86"
              DllEntry="WixQuietExec"
              Execute="deferred"
              Return="ignore"
              Impersonate="yes" />
```

`Return="ignore"` is deliberate per the goal "registration failures do not fail the MSI install." Per-writer detail goes to `%TEMP%\SaddleRAG-register.log`.

Same shape for `UnregisterAiClients`. Conditions:

```xml
<Custom Action="RegisterAiClients"   After="PatchAppSettings" Condition="NOT Installed OR REINSTALL" />
<Custom Action="UnregisterAiClients" Before="RemoveFiles"     Condition="REMOVE = &quot;ALL&quot;" />
```

This pattern handles fresh install, repair (`REINSTALL=ALL`), `msiexec /fa`, major upgrade (uninstall-then-install dance), and uninstall.

**Files removed from the installer payload:** [SaddleRAG.Installer/RegisterClaudePlugin.ps1](../../../SaddleRAG.Installer/RegisterClaudePlugin.ps1) and [SaddleRAG.Installer/UnregisterClaudePlugin.ps1](../../../SaddleRAG.Installer/UnregisterClaudePlugin.ps1).

## Testing

Five layers, all in `SaddleRAG.Tests` so they're picked up by the standard `dotnet test SaddleRAG.Tests` (no filter required).

**1. Per-writer fixture-based unit tests.** Each writer has a folder of fixtures with paired input/expected configs:

```
Fixtures/claude-code/
    empty/                          # file does not exist
        expected-after-register.json
    no-mcp-section/                 # file exists, no mcpServers key
        input.json
        expected-after-register.json
    other-servers-only/             # has unrelated MCP entries; saddlerag absent
        input.json
        expected-after-register.json
    existing-saddlerag/             # stale entry; should be overwritten
        input.json
        expected-after-register.json
    existing-permissions-allow/     # already has permissions.allow with other tools
        input.json
        expected-after-register.json
    malformed-json/
        input.json
        expected-error.txt          # file unmodified
```

Equivalent fixtures for each of the other three writers, sized to that tool's schema.

**2. Round-trip tests.** For every fixture, run `Register` then `Unregister` and assert the final file is byte-identical to the input (or absent if input was absent). This is the explicit "the install cleans up after itself" guarantee — automated, not manual.

**3. `ClientRegistrar` orchestration tests.**
- All writers succeed → exit 0
- One injected failure → others still run, exit 2, failure noted
- Per-tool flags filter writers correctly

**4. CLI subcommand tests.** Argument parsing, exit-code mapping, `--json` output stable schema (snapshot tests).

**5. End-to-end test (single integration test).** Creates a fake `%USERPROFILE%` and `%APPDATA%` in a temp dir, runs `register-clients` with all flags on, asserts every expected entry in every expected file, runs `unregister-clients`, asserts every file is back to its pre-register state. One test, sub-second, covers the whole orchestration in one shot.

**Not automated:** the MSI itself. Custom-action behavior is verified by hand (existing prior-plan Task 5 pattern). The C# tests cover the registration logic that the MSI shells out to; the CA wrapper is small enough to eyeball.

## One-time hand cleanup

Three artifacts of the current scheme that the new design makes obsolete. None belong in the MSI — they're either repo edits or per-machine state from prior experiments.

1. **Remove [.mcp.json](../../../.mcp.json)** from the repo. Once the global writer is wiring `~/.claude.json`, the project-scoped `.mcp.json` causes the duplicate-registration footgun. Done as a single commit.
2. **Remove [plugin/](../../../plugin/) from the repo.** The skill content moves to `SaddleRAG.ClientIntegration/Resources/saddlerag-first.md` (embedded resource); useful README content folds into the main `README.md`; the rest of `plugin/` becomes dead. Done as part of the implementation tasks.
3. **Clean the dangling `saddlerag@local` registration** in the user's `~/.claude/plugins/installed_plugins.json`:

   ```powershell
   claude plugin uninstall saddlerag
   ```

   If `claude` errors on the dangling entry, the manual edit is to delete the `"saddlerag@local"` array entry from `installed_plugins.json`. Done once, by the user, after the new install scheme lands and is verified.

## Compatibility / migration

- **Existing users with the old MSI installed:** their next MSI upgrade triggers `UnregisterAiClients` (old → no-op for keys that don't exist) then `RegisterAiClients` (new → writes the right files). The wrong-file legacy entry under `~/.claude/settings.json` is left alone (out of scope per Non-Goals). Hand cleanup item 3 covers their dangling `installed_plugins.json` if they have one.
- **`mcp-remote` requirement.** The Claude Desktop writer continues to use `npx -y mcp-remote@latest`. Desktop has no native HTTP MCP transport, so the stdio bridge is unavoidable. Users without `npx` (no Node installed) will see a Desktop-side connection failure, not a registration failure. Documented in the README's troubleshooting section.
- **Service must be running for any client to connect.** The MSI starts the `SaddleRAGMcp` service at install time (existing behavior). If the service is stopped, all four clients see "connection refused" — same failure mode as today.

## Risks

| Risk | Mitigation |
|---|---|
| Copilot CLI's config schema is unstable or undocumented | First task of implementation is a spike; writer ships off by default until verified |
| VSCode `mcp.json` schema changes in a future VSCode release | Per-writer unit tests catch regressions when we update; format is well-established as of 1.102 |
| User has hand-edited `permissions.allow[]` with conflicting entries | Writer dedupes and merges; never removes entries it didn't add |
| MSI custom action runs as wrong user (config writes to SYSTEM profile, not user's) | `Impersonate="yes"` on the CA, same pattern proven in the existing PS1-based CAs |
| `SaddleRAG.Cli.exe` startup cost in the CA bloats install time | Subcommand path avoids DB / Ingestion init; should run in well under a second; measured during implementation |
