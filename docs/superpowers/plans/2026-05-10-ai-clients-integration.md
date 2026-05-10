# AI Clients Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire SaddleRAG into Claude Code, Claude Desktop, VSCode (Copilot Chat MCP), and GitHub Copilot CLI from one MSI install — registration logic in a new testable C# project, MSI custom actions become one-line shell-outs to a `SaddleRAG.Cli` subcommand.

**Architecture:** New `SaddleRAG.ClientIntegration` project hosts one writer per AI tool behind a common `IClientWriter` interface plus a `ClientRegistrar` orchestrator. `SaddleRAG.Cli` exposes `register-clients`, `unregister-clients`, and `status` subcommands. The MSI's `AiClientsDlg` collects four checkboxes; two custom actions invoke `SaddleRAG.Cli.exe` for register / unregister. Skill content (`saddlerag-first.md`) moves from the repo's `plugin/` folder to an embedded resource on the new project.

**Tech Stack:** .NET 10, xUnit v3 + NSubstitute, System.CommandLine 2.0.7, WiX Toolset 5 (WixUI + WixUtil extensions), PowerShell only as a verification harness in the smoke test.

**Spec:** [docs/superpowers/specs/2026-05-10-ai-clients-integration-design.md](../specs/2026-05-10-ai-clients-integration-design.md)

**Branch:** `claude/ai-clients-integration` (already created, design spec already committed)

---

## File Map

| File | Change | Notes |
|---|---|---|
| `SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj` | Create | New project |
| `SaddleRAG.ClientIntegration/IClientWriter.cs` | Create | Common contract |
| `SaddleRAG.ClientIntegration/ClientRegistrar.cs` | Create | Orchestrator |
| `SaddleRAG.ClientIntegration/Models/SaddleRagEndpoint.cs` | Create | Canonical endpoint values |
| `SaddleRAG.ClientIntegration/Models/RegisterResult.cs` | Create | Per-writer outcome |
| `SaddleRAG.ClientIntegration/Models/UnregisterResult.cs` | Create | Per-writer outcome |
| `SaddleRAG.ClientIntegration/Models/StatusResult.cs` | Create | Per-writer status |
| `SaddleRAG.ClientIntegration/Models/RegistrarResult.cs` | Create | Aggregate orchestrator outcome |
| `SaddleRAG.ClientIntegration/Writers/ClaudeCodeWriter.cs` | Create | `~/.claude.json` + skill drop |
| `SaddleRAG.ClientIntegration/Writers/ClaudeDesktopWriter.cs` | Create | `claude_desktop_config.json` |
| `SaddleRAG.ClientIntegration/Writers/VsCodeMcpWriter.cs` | Create | `mcp.json` |
| `SaddleRAG.ClientIntegration/Writers/CopilotCliWriter.cs` | Create | Behind feature flag until spike |
| `SaddleRAG.ClientIntegration/Resources/saddlerag-first.md` | Create | Moved from `plugin/skills/saddlerag-first/SKILL.md` |
| `SaddleRAG.Cli/SaddleRAG.Cli.csproj` | Modify | Add `SaddleRAG.ClientIntegration` project ref |
| `SaddleRAG.Cli/Commands/RegisterClientsCommand.cs` | Create | New subcommand surface |
| `SaddleRAG.Cli/Commands/UnregisterClientsCommand.cs` | Create | New subcommand surface |
| `SaddleRAG.Cli/Commands/StatusCommand.cs` | Create | New subcommand surface |
| `SaddleRAG.Cli/Program.cs` | Modify | Register the three new subcommands |
| `SaddleRAG.Tests/SaddleRAG.Tests.csproj` | Modify | Add `SaddleRAG.ClientIntegration` project ref |
| `SaddleRAG.Tests/ClientIntegration/*` | Create | Per-writer + orchestrator + CLI + e2e tests + fixtures |
| `SaddleRAG.slnx` | Modify | Add `SaddleRAG.ClientIntegration` project entry |
| `SaddleRAG.Installer/Package.wxs` | Modify | Rename dialog, four properties, swap CAs to `WixQuietExec` of `SaddleRAG.Cli.exe` |
| `SaddleRAG.Installer/RegisterClaudePlugin.ps1` | Delete | Replaced by CLI subcommand |
| `SaddleRAG.Installer/UnregisterClaudePlugin.ps1` | Delete | Replaced by CLI subcommand |
| `.mcp.json` | Delete | Global install supersedes |
| `plugin/` | Delete | Skill moved to embedded resource; rest is dead |
| `README.md` | Modify | Update install + troubleshooting sections |
| `docs/superpowers/notes/copilot-cli-config.md` | Create | Output of Task 8 spike |

---

## Phase 1: Foundation

### Task 1: Create SaddleRAG.ClientIntegration project skeleton

**Files:**
- Create: `SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj`
- Modify: `SaddleRAG.slnx`

- [ ] **Step 1: Create the project file**

Write `SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CodeStructure.Analyzers" Version="1.0.7" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Resources\*.md" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution**

Modify `SaddleRAG.slnx`. Find the existing `<Project>` lines and add the new project alphabetically:

```xml
  <Project Path="SaddleRAG.Cli/SaddleRAG.Cli.csproj" />
  <Project Path="SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj" />
  <Project Path="SaddleRAG.Core/SaddleRAG.Core.csproj" />
```

- [ ] **Step 3: Verify the solution builds**

Run: `dotnet build SaddleRAG.slnx`
Expected: build succeeds, new project compiled (will warn about no source files — that's fine).

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
feat(client-integration): scaffold SaddleRAG.ClientIntegration project

Empty project skeleton; subsequent tasks fill in the contract,
writers, and tests.
```

Run:
```
git add SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj SaddleRAG.slnx
git commit -F msg.txt
```

---

### Task 2: Define result models

**Files:**
- Create: `SaddleRAG.ClientIntegration/Models/SaddleRagEndpoint.cs`
- Create: `SaddleRAG.ClientIntegration/Models/RegisterResult.cs`
- Create: `SaddleRAG.ClientIntegration/Models/UnregisterResult.cs`
- Create: `SaddleRAG.ClientIntegration/Models/StatusResult.cs`
- Create: `SaddleRAG.ClientIntegration/Models/RegistrarResult.cs`

- [ ] **Step 1: Write `SaddleRagEndpoint.cs`**

```csharp
// SaddleRagEndpoint.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record SaddleRagEndpoint(
    string Url,
    int TimeoutSeconds,
    IReadOnlyList<string> ReadOnlyToolPermissions)
{
    public static SaddleRagEndpoint Default { get; } = new(
        Url: "http://localhost:6100/mcp",
        TimeoutSeconds: 300,
        ReadOnlyToolPermissions: new[]
        {
            "mcp__saddlerag__search_docs",
            "mcp__saddlerag__get_class_reference",
            "mcp__saddlerag__get_library_overview",
            "mcp__saddlerag__get_library_health",
            "mcp__saddlerag__get_dashboard_index",
            "mcp__saddlerag__get_server_logs",
            "mcp__saddlerag__get_version_changes",
            "mcp__saddlerag__get_job_status",
            "mcp__saddlerag__get_scrape_status",
            "mcp__saddlerag__get_rescrub_status",
            "mcp__saddlerag__list_libraries",
            "mcp__saddlerag__list_pages",
            "mcp__saddlerag__list_symbols",
            "mcp__saddlerag__list_excluded_symbols",
            "mcp__saddlerag__list_jobs",
            "mcp__saddlerag__list_scrape_jobs",
            "mcp__saddlerag__list_rescrub_jobs",
            "mcp__saddlerag__list_profiles",
            "mcp__saddlerag__inspect_scrape",
            "mcp__saddlerag__recon_library"
        });
}
```

- [ ] **Step 2: Write `RegisterResult.cs`**

```csharp
// RegisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record RegisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    string? SkillPath = null)
{
    public static RegisterResult Ok(string clientName, string configPath, string message, string? skillPath = null)
        => new(clientName, true, configPath, message, skillPath);

    public static RegisterResult Failed(string clientName, string configPath, string message)
        => new(clientName, false, configPath, message);
}
```

- [ ] **Step 3: Write `UnregisterResult.cs`**

```csharp
// UnregisterResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record UnregisterResult(
    string ClientName,
    bool Success,
    string ConfigPath,
    string Message,
    bool WasNoOp)
{
    public static UnregisterResult Removed(string clientName, string configPath, string message)
        => new(clientName, true, configPath, message, WasNoOp: false);

    public static UnregisterResult NoOp(string clientName, string configPath, string reason)
        => new(clientName, true, configPath, reason, WasNoOp: true);

    public static UnregisterResult Failed(string clientName, string configPath, string message)
        => new(clientName, false, configPath, message, WasNoOp: false);
}
```

- [ ] **Step 4: Write `StatusResult.cs`**

```csharp
// StatusResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record StatusResult(
    string ClientName,
    string ConfigPath,
    bool ConfigFileExists,
    bool SaddleRagEntryPresent,
    bool EndpointMatchesCanonical,
    bool? SkillFilePresent,
    string Notes);
```

- [ ] **Step 5: Write `RegistrarResult.cs`**

```csharp
// RegistrarResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.ClientIntegration.Models;

public sealed record RegistrarResult(
    IReadOnlyList<RegisterResult> RegisterResults,
    IReadOnlyList<UnregisterResult> UnregisterResults)
{
    public bool AllRegisterSucceeded => RegisterResults.All(r => r.Success);
    public bool AllUnregisterSucceeded => UnregisterResults.All(r => r.Success);

    public static RegistrarResult ForRegister(IReadOnlyList<RegisterResult> results)
        => new(results, Array.Empty<UnregisterResult>());

    public static RegistrarResult ForUnregister(IReadOnlyList<UnregisterResult> results)
        => new(Array.Empty<RegisterResult>(), results);
}
```

- [ ] **Step 6: Verify the build**

Run: `dotnet build SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj`
Expected: build succeeds.

- [ ] **Step 7: Commit**

Write `msg.txt`:
```
feat(client-integration): add result models and SaddleRagEndpoint

Records carry per-writer success/failure and the canonical endpoint
(URL, timeout, read-only tool permissions list) used by every writer.
```

Run:
```
git add SaddleRAG.ClientIntegration/Models/
git commit -F msg.txt
```

---

### Task 3: Define the `IClientWriter` interface

**Files:**
- Create: `SaddleRAG.ClientIntegration/IClientWriter.cs`

- [ ] **Step 1: Write the interface**

```csharp
// IClientWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

public interface IClientWriter
{
    string ClientName { get; }

    Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct);

    Task<UnregisterResult> UnregisterAsync(CancellationToken ct);

    Task<StatusResult> GetStatusAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Verify the build**

Run: `dotnet build SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Write `msg.txt`:
```
feat(client-integration): define IClientWriter contract

Single interface every per-tool writer implements; one method each
for register, unregister, and status.
```

Run:
```
git add SaddleRAG.ClientIntegration/IClientWriter.cs
git commit -F msg.txt
```

---

### Task 4: Move `saddlerag-first.md` to embedded resource

**Files:**
- Create: `SaddleRAG.ClientIntegration/Resources/saddlerag-first.md`
- Delete: `plugin/skills/saddlerag-first/SKILL.md` (whole `plugin/` removed in Task 21; this task only relocates content)

- [ ] **Step 1: Read the existing skill content**

Read `plugin/skills/saddlerag-first/SKILL.md` and copy its complete content (all lines, including the YAML front-matter) to a clipboard or temp variable.

- [ ] **Step 2: Write the embedded-resource copy**

Write `SaddleRAG.ClientIntegration/Resources/saddlerag-first.md` with the **exact** content of `plugin/skills/saddlerag-first/SKILL.md`. Do not re-author; verbatim copy.

- [ ] **Step 3: Verify the file is picked up as an embedded resource**

Run: `dotnet build SaddleRAG.ClientIntegration/SaddleRAG.ClientIntegration.csproj`
Then: open the built DLL and confirm the resource is embedded:
```
dotnet exec --runtimeconfig SaddleRAG.ClientIntegration/bin/Debug/net10.0/SaddleRAG.ClientIntegration.runtimeconfig.json -- /c "Console.WriteLine(string.Join(\",\", typeof(SaddleRAG.ClientIntegration.Models.SaddleRagEndpoint).Assembly.GetManifestResourceNames()))"
```
Easier: write a quick xUnit test in Task 5 that loads the resource. For now, trust the csproj `<EmbeddedResource Include="Resources\*.md" />` from Task 1.

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
feat(client-integration): embed saddlerag-first skill content

The skill content moves from plugin/skills/saddlerag-first/SKILL.md
(removed in a later task) into an embedded resource on the new
project so writers can stamp it into ~/.claude/skills/ at install
time without disk lookups.
```

Run:
```
git add SaddleRAG.ClientIntegration/Resources/saddlerag-first.md
git commit -F msg.txt
```

(Note: `plugin/skills/saddlerag-first/SKILL.md` stays in place until Task 21 — keeps each commit self-consistent.)

---

## Phase 2: Writers (TDD per writer)

Every writer task follows the same pattern: write fixture pairs → write failing test → verify it fails → implement writer → verify tests pass → commit. Steps spelled out fully for the first writer (Task 5); later writers reference the same template, but the actual code, fixtures, and exact expected outputs are spelled out in full per task.

### Task 5: `ClaudeCodeWriter` — fixtures, tests, implementation

**Files:**
- Modify: `SaddleRAG.Tests/SaddleRAG.Tests.csproj`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/empty/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/no-mcp-section/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/no-mcp-section/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/other-servers-only/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/other-servers-only/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-saddlerag/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-saddlerag/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-permissions-allow/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-permissions-allow/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/malformed-json/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/ClaudeCodeWriterTests.cs`
- Create: `SaddleRAG.Tests/ClientIntegration/TestPaths.cs` (helper for finding fixtures)
- Create: `SaddleRAG.ClientIntegration/Writers/ClaudeCodeWriter.cs`

- [ ] **Step 1: Add the project reference and fixture-copy rule to the test csproj**

Modify `SaddleRAG.Tests/SaddleRAG.Tests.csproj`. Find:
```xml
  <ItemGroup>
    <ProjectReference Include="..\SaddleRAG.Core\SaddleRAG.Core.csproj" />
    <ProjectReference Include="..\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj" />
    <ProjectReference Include="..\SaddleRAG.Mcp\SaddleRAG.Mcp.csproj" />
    <ProjectReference Include="..\SaddleRAG.Monitor\SaddleRAG.Monitor.csproj" />
  </ItemGroup>
```

Replace with (add the ClientIntegration ref):
```xml
  <ItemGroup>
    <ProjectReference Include="..\SaddleRAG.ClientIntegration\SaddleRAG.ClientIntegration.csproj" />
    <ProjectReference Include="..\SaddleRAG.Core\SaddleRAG.Core.csproj" />
    <ProjectReference Include="..\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj" />
    <ProjectReference Include="..\SaddleRAG.Mcp\SaddleRAG.Mcp.csproj" />
    <ProjectReference Include="..\SaddleRAG.Monitor\SaddleRAG.Monitor.csproj" />
  </ItemGroup>
```

The existing `<None Update="Fixtures\**\*.json">` rule already covers `ClientIntegration/Fixtures/**` because that's a `**`. Verify it's there:
```xml
  <ItemGroup>
    <None Update="Fixtures\**\*.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

The new fixtures go under `SaddleRAG.Tests/ClientIntegration/Fixtures/...`, not the legacy top-level `SaddleRAG.Tests/Fixtures/...`. Add a second `<None Update>` rule:
```xml
    <None Update="ClientIntegration\Fixtures\**\*.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

- [ ] **Step 2: Write the `TestPaths` helper**

Write `SaddleRAG.Tests/ClientIntegration/TestPaths.cs`:

```csharp
// TestPaths.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Tests.ClientIntegration;

internal static class TestPaths
{
    public static string FixtureDir(string client, string scenario)
        => Path.Combine(AppContext.BaseDirectory, "ClientIntegration", "Fixtures", client, scenario);

    public static string FixtureFile(string client, string scenario, string fileName)
        => Path.Combine(FixtureDir(client, scenario), fileName);
}
```

- [ ] **Step 3: Write fixture files**

Each `expected-after-register.json` is the exact byte-for-byte output the writer must produce. Indented with 2 spaces, UTF-8 no BOM, Unix line endings (the writer uses `JsonSerializer` with `WriteIndented = true`; verify line-ending normalization in tests rather than the fixture file itself).

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/empty/expected-after-register.json`:
```json
{
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 300
    }
  },
  "permissions": {
    "allow": [
      "mcp__saddlerag__search_docs",
      "mcp__saddlerag__get_class_reference",
      "mcp__saddlerag__get_library_overview",
      "mcp__saddlerag__get_library_health",
      "mcp__saddlerag__get_dashboard_index",
      "mcp__saddlerag__get_server_logs",
      "mcp__saddlerag__get_version_changes",
      "mcp__saddlerag__get_job_status",
      "mcp__saddlerag__get_scrape_status",
      "mcp__saddlerag__get_rescrub_status",
      "mcp__saddlerag__list_libraries",
      "mcp__saddlerag__list_pages",
      "mcp__saddlerag__list_symbols",
      "mcp__saddlerag__list_excluded_symbols",
      "mcp__saddlerag__list_jobs",
      "mcp__saddlerag__list_scrape_jobs",
      "mcp__saddlerag__list_rescrub_jobs",
      "mcp__saddlerag__list_profiles",
      "mcp__saddlerag__inspect_scrape",
      "mcp__saddlerag__recon_library"
    ]
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/no-mcp-section/input.json`:
```json
{
  "theme": "dark",
  "skillUsage": {}
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/no-mcp-section/expected-after-register.json`:
```json
{
  "theme": "dark",
  "skillUsage": {},
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 300
    }
  },
  "permissions": {
    "allow": [
      "mcp__saddlerag__search_docs",
      "mcp__saddlerag__get_class_reference",
      "mcp__saddlerag__get_library_overview",
      "mcp__saddlerag__get_library_health",
      "mcp__saddlerag__get_dashboard_index",
      "mcp__saddlerag__get_server_logs",
      "mcp__saddlerag__get_version_changes",
      "mcp__saddlerag__get_job_status",
      "mcp__saddlerag__get_scrape_status",
      "mcp__saddlerag__get_rescrub_status",
      "mcp__saddlerag__list_libraries",
      "mcp__saddlerag__list_pages",
      "mcp__saddlerag__list_symbols",
      "mcp__saddlerag__list_excluded_symbols",
      "mcp__saddlerag__list_jobs",
      "mcp__saddlerag__list_scrape_jobs",
      "mcp__saddlerag__list_rescrub_jobs",
      "mcp__saddlerag__list_profiles",
      "mcp__saddlerag__inspect_scrape",
      "mcp__saddlerag__recon_library"
    ]
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/other-servers-only/input.json`:
```json
{
  "mcpServers": {
    "azure-devops": {
      "command": "npx",
      "args": ["-y", "@azure-devops/mcp", "PenskeShocks"]
    }
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/other-servers-only/expected-after-register.json`:
```json
{
  "mcpServers": {
    "azure-devops": {
      "command": "npx",
      "args": ["-y", "@azure-devops/mcp", "PenskeShocks"]
    },
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 300
    }
  },
  "permissions": {
    "allow": [
      "mcp__saddlerag__search_docs",
      "mcp__saddlerag__get_class_reference",
      "mcp__saddlerag__get_library_overview",
      "mcp__saddlerag__get_library_health",
      "mcp__saddlerag__get_dashboard_index",
      "mcp__saddlerag__get_server_logs",
      "mcp__saddlerag__get_version_changes",
      "mcp__saddlerag__get_job_status",
      "mcp__saddlerag__get_scrape_status",
      "mcp__saddlerag__get_rescrub_status",
      "mcp__saddlerag__list_libraries",
      "mcp__saddlerag__list_pages",
      "mcp__saddlerag__list_symbols",
      "mcp__saddlerag__list_excluded_symbols",
      "mcp__saddlerag__list_jobs",
      "mcp__saddlerag__list_scrape_jobs",
      "mcp__saddlerag__list_rescrub_jobs",
      "mcp__saddlerag__list_profiles",
      "mcp__saddlerag__inspect_scrape",
      "mcp__saddlerag__recon_library"
    ]
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-saddlerag/input.json`:
```json
{
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:9999/mcp",
      "timeout": 60,
      "extraGarbage": "should-be-removed"
    }
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-saddlerag/expected-after-register.json`: same as the `empty/` expected output (proves wholesale overwrite of the saddlerag key per A1 semantics, including removal of `extraGarbage`).

Copy the content of `empty/expected-after-register.json` into `existing-saddlerag/expected-after-register.json`.

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-permissions-allow/input.json`:
```json
{
  "permissions": {
    "allow": [
      "mcp__saddlerag__search_docs",
      "Bash(./customscript.sh)",
      "Edit"
    ]
  }
}
```

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/existing-permissions-allow/expected-after-register.json`:
```json
{
  "permissions": {
    "allow": [
      "mcp__saddlerag__search_docs",
      "Bash(./customscript.sh)",
      "Edit",
      "mcp__saddlerag__get_class_reference",
      "mcp__saddlerag__get_library_overview",
      "mcp__saddlerag__get_library_health",
      "mcp__saddlerag__get_dashboard_index",
      "mcp__saddlerag__get_server_logs",
      "mcp__saddlerag__get_version_changes",
      "mcp__saddlerag__get_job_status",
      "mcp__saddlerag__get_scrape_status",
      "mcp__saddlerag__get_rescrub_status",
      "mcp__saddlerag__list_libraries",
      "mcp__saddlerag__list_pages",
      "mcp__saddlerag__list_symbols",
      "mcp__saddlerag__list_excluded_symbols",
      "mcp__saddlerag__list_jobs",
      "mcp__saddlerag__list_scrape_jobs",
      "mcp__saddlerag__list_rescrub_jobs",
      "mcp__saddlerag__list_profiles",
      "mcp__saddlerag__inspect_scrape",
      "mcp__saddlerag__recon_library"
    ],
    "deny": [
    ]
  },
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 300
    }
  }
}
```

(Note: existing entries preserved in original order; new entries appended in canonical order; no duplicates. `Bash(./customscript.sh)` and `Edit` are unrelated user customizations and stay untouched.)

Write `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-code/malformed-json/input.json`:
```
{not valid json
```

(One line, no closing brace; tests assert the writer surfaces the parse error and leaves the file unmodified.)

- [ ] **Step 4: Write failing tests**

Write `SaddleRAG.Tests/ClientIntegration/ClaudeCodeWriterTests.cs`:

```csharp
// ClaudeCodeWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClaudeCodeWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;
    private readonly string mSkillPath;

    public ClaudeCodeWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, ".claude.json");
        mSkillPath = Path.Combine(mTempDir, ".claude", "skills", "saddlerag-first", "SKILL.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
        {
            Directory.Delete(mTempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("no-mcp-section")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    [InlineData("existing-permissions-allow")]
    public async Task RegisterMatchesFixtureExpectedOutput(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("claude-code", scenario, "input.json");
        if (File.Exists(fixtureInput))
        {
            File.Copy(fixtureInput, mConfigPath, overwrite: true);
        }

        var writer = new ClaudeCodeWriter(mConfigPath, mSkillPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("claude-code", scenario, "expected-after-register.json"));
        string actual = await File.ReadAllTextAsync(mConfigPath);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterDropsSkillFile()
    {
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(File.Exists(mSkillPath));
        string content = await File.ReadAllTextAsync(mSkillPath);
        Assert.Contains("saddlerag-first", content);
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        File.Copy(TestPaths.FixtureFile("claude-code", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath);

        var writer = new ClaudeCodeWriter(mConfigPath, mSkillPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(mConfigPath, result.Message);

        string after = await File.ReadAllTextAsync(mConfigPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        File.Copy(TestPaths.FixtureFile("claude-code", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.WasNoOp);

        string actual = await File.ReadAllTextAsync(mConfigPath);
        Assert.Contains("azure-devops", actual);
        Assert.DoesNotContain("saddlerag", actual);
        Assert.False(File.Exists(mSkillPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new ClaudeCodeWriter(mConfigPath, mSkillPath);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
```

- [ ] **Step 5: Run tests — confirm they fail (writer doesn't exist yet)**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClaudeCodeWriterTests"`
Expected: build error — `ClaudeCodeWriter` not found in `SaddleRAG.ClientIntegration.Writers`.

- [ ] **Step 6: Implement `ClaudeCodeWriter`**

Write `SaddleRAG.ClientIntegration/Writers/ClaudeCodeWriter.cs`:

```csharp
// ClaudeCodeWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class ClaudeCodeWriter : IClientWriter
{
    private const string Name = "claude-code";
    private const string SkillResourceName = "SaddleRAG.ClientIntegration.Resources.saddlerag-first.md";

    private static readonly JsonSerializerOptions psWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };

    private static readonly UTF8Encoding psUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;
    private readonly string mSkillPath;

    public ClaudeCodeWriter(string configPath, string skillPath)
    {
        mConfigPath = configPath;
        mSkillPath = skillPath;
    }

    public string ClientName => Name;

    public static ClaudeCodeWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, ".claude.json");
        string skill = Path.Combine(profile, ".claude", "skills", "saddlerag-first", "SKILL.md");
        return new ClaudeCodeWriter(config, skill);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, "register did not run");
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            ApplyMcpEntry(root, endpoint);
            ApplyPermissionsAllow(root, endpoint.ReadOnlyToolPermissions);
            await SaveRootAsync(root, ct);
            await WriteSkillFileAsync(ct);
            res = RegisterResult.Ok(Name, mConfigPath, "registered", mSkillPath);
        }
        catch (JsonException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
    {
        UnregisterResult res = UnregisterResult.NoOp(Name, mConfigPath, "config file does not exist");
        if (File.Exists(mConfigPath))
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                bool removed = RemoveSaddleRagEntry(root);
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, "saddlerag entry removed");
                }
                else
                {
                    res = UnregisterResult.NoOp(Name, mConfigPath, "saddlerag entry was not present");
                }
            }
            catch (JsonException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
            }
        }
        if (File.Exists(mSkillPath))
        {
            File.Delete(mSkillPath);
            string skillDir = Path.GetDirectoryName(mSkillPath)!;
            if (Directory.Exists(skillDir) && !Directory.EnumerateFileSystemEntries(skillDir).Any())
            {
                Directory.Delete(skillDir);
            }
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool fileExists = File.Exists(mConfigPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root["mcpServers"] as JsonObject;
                JsonObject? entry = servers?["saddlerag"] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                {
                    string? url = entry["url"]?.GetValue<string>();
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (JsonException)
            {
            }
        }
        return new StatusResult(
            ClientName: Name,
            ConfigPath: mConfigPath,
            ConfigFileExists: fileExists,
            SaddleRagEntryPresent: entryPresent,
            EndpointMatchesCanonical: endpointMatches,
            SkillFilePresent: File.Exists(mSkillPath),
            Notes: string.Empty);
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken ct)
    {
        JsonObject root = new();
        if (File.Exists(mConfigPath))
        {
            string text = await File.ReadAllTextAsync(mConfigPath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? parsed = JsonNode.Parse(text);
                if (parsed is JsonObject obj)
                {
                    root = obj;
                }
            }
        }
        return root;
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = mConfigPath + ".tmp";
        string serialized = root.ToJsonString(psWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, psUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }

    private static void ApplyMcpEntry(JsonObject root, SaddleRagEndpoint endpoint)
    {
        JsonObject servers = (root["mcpServers"] as JsonObject) ?? new JsonObject();
        servers["saddlerag"] = new JsonObject
                                   {
                                       ["type"] = "http",
                                       ["url"] = endpoint.Url,
                                       ["timeout"] = endpoint.TimeoutSeconds
                                   };
        root["mcpServers"] = servers;
    }

    private static void ApplyPermissionsAllow(JsonObject root, IReadOnlyList<string> tools)
    {
        JsonObject permissions = (root["permissions"] as JsonObject) ?? new JsonObject();
        JsonArray allow = (permissions["allow"] as JsonArray) ?? new JsonArray();
        HashSet<string> existing = new(StringComparer.Ordinal);
        foreach (JsonNode? node in allow)
        {
            string? value = node?.GetValue<string>();
            if (value is not null)
            {
                existing.Add(value);
            }
        }
        foreach (string tool in tools.Where(t => !existing.Contains(t)))
        {
            allow.Add(tool);
        }
        permissions["allow"] = allow;
        root["permissions"] = permissions;
    }

    private static bool RemoveSaddleRagEntry(JsonObject root)
    {
        bool removed = false;
        if (root["mcpServers"] is JsonObject servers && servers.ContainsKey("saddlerag"))
        {
            servers.Remove("saddlerag");
            removed = true;
        }
        return removed;
    }

    private async Task WriteSkillFileAsync(CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mSkillPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Assembly asm = typeof(ClaudeCodeWriter).Assembly;
        await using Stream? stream = asm.GetManifestResourceStream(SkillResourceName)
                                     ?? throw new InvalidOperationException($"Embedded resource not found: {SkillResourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string content = await reader.ReadToEndAsync(ct);
        await File.WriteAllTextAsync(mSkillPath, content, psUtf8NoBom, ct);
    }
}
```

- [ ] **Step 7: Run tests — confirm they pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClaudeCodeWriterTests"`
Expected: 9 tests pass (5 theory cases + 4 facts).

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test SaddleRAG.Tests`
Expected: previous test count + 9 new tests, all green. (Per the user's "full test suite before commit" rule — no filter.)

- [ ] **Step 9: Commit**

Write `msg.txt`:
```
feat(client-integration): add ClaudeCodeWriter

Writes ~/.claude.json (the right file — fixes the latent
~/.claude/settings.json bug from the prior MSI scheme), drops the
saddlerag-first skill at ~/.claude/skills/saddlerag-first/SKILL.md,
and merges read-only MCP tool permissions into permissions.allow
without disturbing existing entries. Idempotent overwrite of the
saddlerag mcpServers key only.

Tests cover empty file, file without mcpServers, file with other
servers, existing saddlerag entry, existing permissions.allow,
malformed JSON (no-mutation contract), and unregister no-op.
```

Run:
```
git add SaddleRAG.Tests/SaddleRAG.Tests.csproj SaddleRAG.Tests/ClientIntegration/ SaddleRAG.ClientIntegration/Writers/ClaudeCodeWriter.cs
git commit -F msg.txt
```

---

### Task 6: `ClaudeDesktopWriter` — fixtures, tests, implementation

**Files:**
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/empty/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/other-servers-only/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/other-servers-only/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/existing-saddlerag/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/existing-saddlerag/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/malformed-json/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/ClaudeDesktopWriterTests.cs`
- Create: `SaddleRAG.ClientIntegration/Writers/ClaudeDesktopWriter.cs`

- [ ] **Step 1: Write fixtures**

`empty/expected-after-register.json`:
```json
{
  "mcpServers": {
    "saddlerag": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote@latest",
        "http://localhost:6100/mcp",
        "--allow-http"
      ]
    }
  }
}
```

`other-servers-only/input.json`:
```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["@modelcontextprotocol/server-filesystem", "/tmp"]
    }
  }
}
```

`other-servers-only/expected-after-register.json`:
```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["@modelcontextprotocol/server-filesystem", "/tmp"]
    },
    "saddlerag": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote@latest",
        "http://localhost:6100/mcp",
        "--allow-http"
      ]
    }
  }
}
```

`existing-saddlerag/input.json`:
```json
{
  "mcpServers": {
    "saddlerag": {
      "command": "node",
      "args": ["custom-script.js"],
      "env": { "OLD": "1" }
    }
  }
}
```

`existing-saddlerag/expected-after-register.json`: same as `empty/expected-after-register.json`. (Wholesale overwrite removes `env: {OLD: 1}`.)

`malformed-json/input.json`:
```
{not valid json
```

- [ ] **Step 2: Write failing tests**

Write `SaddleRAG.Tests/ClientIntegration/ClaudeDesktopWriterTests.cs`:

```csharp
// ClaudeDesktopWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClaudeDesktopWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;

    public ClaudeDesktopWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-cd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, "claude_desktop_config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
        {
            Directory.Delete(mTempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    public async Task RegisterMatchesFixture(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("claude-desktop", scenario, "input.json");
        if (File.Exists(fixtureInput))
        {
            File.Copy(fixtureInput, mConfigPath, overwrite: true);
        }

        var writer = new ClaudeDesktopWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("claude-desktop", scenario, "expected-after-register.json"));
        string actual = await File.ReadAllTextAsync(mConfigPath);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterWritesFileWithoutBom()
    {
        var writer = new ClaudeDesktopWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(mConfigPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Claude Desktop rejects UTF-8 BOM; writer must not emit one.");
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        File.Copy(TestPaths.FixtureFile("claude-desktop", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath);

        var writer = new ClaudeDesktopWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath));
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        File.Copy(TestPaths.FixtureFile("claude-desktop", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new ClaudeDesktopWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("filesystem", await File.ReadAllTextAsync(mConfigPath));
        Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mConfigPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new ClaudeDesktopWriter(mConfigPath);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
```

- [ ] **Step 3: Run tests — confirm they fail (writer doesn't exist yet)**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClaudeDesktopWriterTests"`
Expected: build error — `ClaudeDesktopWriter` not found.

- [ ] **Step 4: Implement `ClaudeDesktopWriter`**

Write `SaddleRAG.ClientIntegration/Writers/ClaudeDesktopWriter.cs`:

```csharp
// ClaudeDesktopWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class ClaudeDesktopWriter : IClientWriter
{
    private const string Name = "claude-desktop";

    private static readonly JsonSerializerOptions psWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };
    private static readonly UTF8Encoding psUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;

    public ClaudeDesktopWriter(string configPath)
    {
        mConfigPath = configPath;
    }

    public string ClientName => Name;

    public static ClaudeDesktopWriter ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = Path.Combine(profile, "AppData", "Roaming", "Claude", "claude_desktop_config.json");
        return new ClaudeDesktopWriter(config);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, "register did not run");
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            JsonObject servers = (root["mcpServers"] as JsonObject) ?? new JsonObject();
            servers["saddlerag"] = new JsonObject
                                       {
                                           ["command"] = "npx",
                                           ["args"] = new JsonArray("-y", "mcp-remote@latest", endpoint.Url, "--allow-http")
                                       };
            root["mcpServers"] = servers;
            await SaveRootAsync(root, ct);
            res = RegisterResult.Ok(Name, mConfigPath, "registered");
        }
        catch (JsonException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
    {
        UnregisterResult res = UnregisterResult.NoOp(Name, mConfigPath, "config file does not exist");
        if (File.Exists(mConfigPath))
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                bool removed = false;
                if (root["mcpServers"] is JsonObject servers && servers.ContainsKey("saddlerag"))
                {
                    servers.Remove("saddlerag");
                    removed = true;
                }
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, "saddlerag entry removed");
                }
                else
                {
                    res = UnregisterResult.NoOp(Name, mConfigPath, "saddlerag entry was not present");
                }
            }
            catch (JsonException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
            }
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool fileExists = File.Exists(mConfigPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root["mcpServers"] as JsonObject;
                JsonObject? entry = servers?["saddlerag"] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                {
                    JsonArray? args = entry["args"] as JsonArray;
                    bool hasUrl = false;
                    if (args is not null)
                    {
                        foreach (JsonNode? node in args)
                        {
                            string? value = node?.GetValue<string>();
                            if (string.Equals(value, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal))
                            {
                                hasUrl = true;
                            }
                        }
                    }
                    endpointMatches = hasUrl;
                }
            }
            catch (JsonException)
            {
            }
        }
        return new StatusResult(
            ClientName: Name,
            ConfigPath: mConfigPath,
            ConfigFileExists: fileExists,
            SaddleRagEntryPresent: entryPresent,
            EndpointMatchesCanonical: endpointMatches,
            SkillFilePresent: null,
            Notes: "Claude Desktop has no skill concept");
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken ct)
    {
        JsonObject root = new();
        if (File.Exists(mConfigPath))
        {
            string text = await File.ReadAllTextAsync(mConfigPath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? parsed = JsonNode.Parse(text);
                if (parsed is JsonObject obj)
                {
                    root = obj;
                }
            }
        }
        return root;
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = mConfigPath + ".tmp";
        string serialized = root.ToJsonString(psWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, psUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }
}
```

- [ ] **Step 5: Run tests — verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClaudeDesktopWriterTests"`
Expected: 8 tests pass.

- [ ] **Step 6: Run full suite**

Run: `dotnet test SaddleRAG.Tests`
Expected: all green.

- [ ] **Step 7: Commit**

Write `msg.txt`:
```
feat(client-integration): add ClaudeDesktopWriter

Writes %APPDATA%\Claude\claude_desktop_config.json with the
mcp-remote stdio bridge entry (Desktop's schema only supports stdio).
UTF-8 without BOM (Desktop's parser rejects BOM). Idempotent
overwrite of saddlerag key, surgical removal on unregister.
```

Run:
```
git add SaddleRAG.Tests/ClientIntegration/ClaudeDesktopWriterTests.cs SaddleRAG.Tests/ClientIntegration/Fixtures/claude-desktop/ SaddleRAG.ClientIntegration/Writers/ClaudeDesktopWriter.cs
git commit -F msg.txt
```

---

### Task 7: `VsCodeMcpWriter` — fixtures, tests, implementation

**Files:**
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/empty/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/other-servers-only/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/other-servers-only/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/existing-saddlerag/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/existing-saddlerag/expected-after-register.json`
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/malformed-json/input.json`
- Create: `SaddleRAG.Tests/ClientIntegration/VsCodeMcpWriterTests.cs`
- Create: `SaddleRAG.ClientIntegration/Writers/VsCodeMcpWriter.cs`

- [ ] **Step 1: Write fixtures**

`empty/expected-after-register.json`:
```json
{
  "servers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp"
    }
  }
}
```

`other-servers-only/input.json`:
```json
{
  "servers": {
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp/"
    }
  }
}
```

`other-servers-only/expected-after-register.json`:
```json
{
  "servers": {
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp/"
    },
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp"
    }
  }
}
```

`existing-saddlerag/input.json`:
```json
{
  "servers": {
    "saddlerag": {
      "type": "http",
      "url": "http://wrong-host:9999/mcp",
      "extraGarbage": "remove me"
    }
  }
}
```

`existing-saddlerag/expected-after-register.json`: identical to `empty/expected-after-register.json`.

`malformed-json/input.json`:
```
{not valid json
```

- [ ] **Step 2: Write failing tests**

Write `SaddleRAG.Tests/ClientIntegration/VsCodeMcpWriterTests.cs`:

```csharp
// VsCodeMcpWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class VsCodeMcpWriterTests : IDisposable
{
    private readonly string mTempDir;
    private readonly string mConfigPath;

    public VsCodeMcpWriterTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-vsc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
        mConfigPath = Path.Combine(mTempDir, "Code", "User", "mcp.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
        {
            Directory.Delete(mTempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-servers-only")]
    [InlineData("existing-saddlerag")]
    public async Task RegisterMatchesFixture(string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile("vscode-mcp", scenario, "input.json");
        if (File.Exists(fixtureInput))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mConfigPath)!);
            File.Copy(fixtureInput, mConfigPath, overwrite: true);
        }

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.Success, result.Message);

        string expected = await File.ReadAllTextAsync(
            TestPaths.FixtureFile("vscode-mcp", scenario, "expected-after-register.json"));
        string actual = await File.ReadAllTextAsync(mConfigPath);

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

    [Fact]
    public async Task RegisterCreatesParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(mConfigPath)!));

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(mConfigPath));
    }

    [Fact]
    public async Task RegisterMalformedJsonReturnsFailureAndLeavesFileUntouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mConfigPath)!);
        File.Copy(TestPaths.FixtureFile("vscode-mcp", "malformed-json", "input.json"), mConfigPath, overwrite: true);
        string before = await File.ReadAllTextAsync(mConfigPath);

        var writer = new VsCodeMcpWriter(mConfigPath);
        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(mConfigPath));
    }

    [Fact]
    public async Task UnregisterRemovesSaddleRagOnly()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mConfigPath)!);
        File.Copy(TestPaths.FixtureFile("vscode-mcp", "other-servers-only", "input.json"), mConfigPath, overwrite: true);
        var writer = new VsCodeMcpWriter(mConfigPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("github", await File.ReadAllTextAsync(mConfigPath));
        Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mConfigPath));
    }

    [Fact]
    public async Task UnregisterMissingFileIsNoOp()
    {
        var writer = new VsCodeMcpWriter(mConfigPath);

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
```

- [ ] **Step 3: Run tests — confirm they fail**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~VsCodeMcpWriterTests"`
Expected: build error.

- [ ] **Step 4: Implement `VsCodeMcpWriter`**

Write `SaddleRAG.ClientIntegration/Writers/VsCodeMcpWriter.cs`:

```csharp
// VsCodeMcpWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class VsCodeMcpWriter : IClientWriter
{
    private const string Name = "vscode-mcp";

    private static readonly JsonSerializerOptions psWriteOptions = new()
                                                                       {
                                                                           WriteIndented = true
                                                                       };
    private static readonly UTF8Encoding psUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string mConfigPath;

    public VsCodeMcpWriter(string configPath)
    {
        mConfigPath = configPath;
    }

    public string ClientName => Name;

    public static VsCodeMcpWriter ForCurrentUser()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string config = Path.Combine(appData, "Code", "User", "mcp.json");
        return new VsCodeMcpWriter(config);
    }

    public async Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res = RegisterResult.Failed(Name, mConfigPath, "register did not run");
        try
        {
            JsonObject root = await LoadRootAsync(ct);
            JsonObject servers = (root["servers"] as JsonObject) ?? new JsonObject();
            servers["saddlerag"] = new JsonObject
                                       {
                                           ["type"] = "http",
                                           ["url"] = endpoint.Url
                                       };
            root["servers"] = servers;
            await SaveRootAsync(root, ct);
            res = RegisterResult.Ok(Name, mConfigPath, "registered");
        }
        catch (JsonException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            res = RegisterResult.Failed(Name, mConfigPath, $"I/O error on {mConfigPath}: {ex.Message}");
        }
        return res;
    }

    public async Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
    {
        UnregisterResult res = UnregisterResult.NoOp(Name, mConfigPath, "config file does not exist");
        if (File.Exists(mConfigPath))
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                bool removed = false;
                if (root["servers"] is JsonObject servers && servers.ContainsKey("saddlerag"))
                {
                    servers.Remove("saddlerag");
                    removed = true;
                }
                if (removed)
                {
                    await SaveRootAsync(root, ct);
                    res = UnregisterResult.Removed(Name, mConfigPath, "saddlerag entry removed");
                }
                else
                {
                    res = UnregisterResult.NoOp(Name, mConfigPath, "saddlerag entry was not present");
                }
            }
            catch (JsonException ex)
            {
                res = UnregisterResult.Failed(Name, mConfigPath, $"failed to parse {mConfigPath}: {ex.Message}");
            }
        }
        return res;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct)
    {
        bool fileExists = File.Exists(mConfigPath);
        bool entryPresent = false;
        bool endpointMatches = false;
        if (fileExists)
        {
            try
            {
                JsonObject root = await LoadRootAsync(ct);
                JsonObject? servers = root["servers"] as JsonObject;
                JsonObject? entry = servers?["saddlerag"] as JsonObject;
                entryPresent = entry is not null;
                if (entry is not null)
                {
                    string? url = entry["url"]?.GetValue<string>();
                    endpointMatches = string.Equals(url, SaddleRagEndpoint.Default.Url, StringComparison.Ordinal);
                }
            }
            catch (JsonException)
            {
            }
        }
        return new StatusResult(
            ClientName: Name,
            ConfigPath: mConfigPath,
            ConfigFileExists: fileExists,
            SaddleRagEntryPresent: entryPresent,
            EndpointMatchesCanonical: endpointMatches,
            SkillFilePresent: null,
            Notes: "VSCode MCP has no skill concept");
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken ct)
    {
        JsonObject root = new();
        if (File.Exists(mConfigPath))
        {
            string text = await File.ReadAllTextAsync(mConfigPath, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                JsonNode? parsed = JsonNode.Parse(text);
                if (parsed is JsonObject obj)
                {
                    root = obj;
                }
            }
        }
        return root;
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(mConfigPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = mConfigPath + ".tmp";
        string serialized = root.ToJsonString(psWriteOptions);
        await File.WriteAllTextAsync(tmp, serialized, psUtf8NoBom, ct);
        File.Move(tmp, mConfigPath, overwrite: true);
    }
}
```

- [ ] **Step 5: Run tests — verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~VsCodeMcpWriterTests"`
Expected: 8 tests pass.

- [ ] **Step 6: Run full suite**

Run: `dotnet test SaddleRAG.Tests`
Expected: all green.

- [ ] **Step 7: Commit**

Write `msg.txt`:
```
feat(client-integration): add VsCodeMcpWriter

Writes %APPDATA%\Code\User\mcp.json with the dedicated VSCode MCP
schema (servers.<name>.url + type). Picked up by VSCode 1.102+
(GitHub Copilot Chat MCP and any other MCP-aware extension) without
touching settings.json.
```

Run:
```
git add SaddleRAG.Tests/ClientIntegration/VsCodeMcpWriterTests.cs SaddleRAG.Tests/ClientIntegration/Fixtures/vscode-mcp/ SaddleRAG.ClientIntegration/Writers/VsCodeMcpWriter.cs
git commit -F msg.txt
```

---

### Task 8: Spike — verify GitHub Copilot CLI config layout

**Files:**
- Create: `docs/superpowers/notes/copilot-cli-config.md`

The CopilotCliWriter (Task 9) needs to know where Copilot CLI keeps per-user MCP config and skill files on Windows. This task produces a written record so Task 9 can be authored confidently.

- [ ] **Step 1: Detect what's installed**

Run each, record what's installed and at what version:
```
gh --version
gh extension list
copilot --version
```

(`gh copilot` is the older `gh` extension — CLI command `gh copilot suggest`. `copilot` standalone is the newer agentic Copilot CLI shipped late 2025.)

- [ ] **Step 2: If neither is installed, install the newer agentic CLI**

```
winget install --id GitHub.CopilotCLI -e
```

Re-open the shell to pick up PATH changes; rerun `copilot --version`.

- [ ] **Step 3: Inspect the per-user config locations**

```
copilot --help
```
Look for an `mcp` subcommand or config-path documentation. Then inspect the likely candidates:
```
ls $env:LOCALAPPDATA\github-copilot
ls $env:APPDATA\github-copilot
ls $env:USERPROFILE\.copilot
ls $env:USERPROFILE\.config\github-copilot
```

Look for files with names like `mcp.json`, `config.json`, `settings.json`. Open any found files and inspect their schema for an `mcpServers`, `servers`, `mcp.servers`, or similar key.

If `copilot` provides a command for adding MCP servers (e.g., `copilot mcp add <name> --url <url>`), prefer that — invoke it with a dummy entry to produce a known-good output you can inspect:
```
copilot mcp add saddlerag-spike --type http --url http://localhost:6100/mcp
```
Then look at what file changed — `Get-ChildItem $env:USERPROFILE -Recurse -Filter "*.json" -Force | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) }`.
Remove the spike entry: `copilot mcp remove saddlerag-spike` (or whatever the actual command is).

- [ ] **Step 4: Inspect skill location**

If Copilot CLI supports skills, look for a `skills/` directory under the same per-user config root. Drop a test skill file (`copilot-skill-spike.md`) and verify Copilot picks it up via `copilot --help` listing skills, or by running an interactive prompt that should trigger it.

- [ ] **Step 5: Document findings**

Write `docs/superpowers/notes/copilot-cli-config.md`:

```markdown
# GitHub Copilot CLI — config layout (Windows, as of <date>)

## CLI inspected
- `copilot --version`: <e.g. 0.4.2>
- `gh --version` / `gh copilot --version`: <if relevant>
- Install method: <winget / gh extension / etc.>

## MCP config
- Path: `<absolute path>`
- Schema (example):
  ```json
  { "...": "..." }
  ```
- Idempotent overwrite key: `<servers.saddlerag or whatever>`
- Encoding: <UTF-8 BOM behavior>

## Skill config
- Path: `<absolute path or "no skill concept on Windows">`
- Schema / file format: <description>
- How Copilot discovers skills: <description>

## Findings summary

- [ ] Copilot CLI HAS native MCP support on Windows → Task 9 implements the writer normally
- [ ] Copilot CLI has no MCP support → Task 9 ships a stub writer that no-ops with a "not supported on this platform" status; dialog checkbox is disabled with a tooltip

## Risks / open questions
- <anything you couldn't determine>
```

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
docs(spike): document Copilot CLI config layout for client integration

Records the per-user MCP config path, schema, and skill-delivery
mechanism (or absence) on Windows, so the CopilotCliWriter task can
proceed with a verified target instead of guessing.
```

Run:
```
git add docs/superpowers/notes/copilot-cli-config.md
git commit -F msg.txt
```

---

### Task 9: `CopilotCliWriter` — fixtures, tests, implementation (or stub)

**Files (if spike confirmed support):**
- Create: `SaddleRAG.Tests/ClientIntegration/Fixtures/copilot-cli/...` (mirror Task 6 / 7 fixture set)
- Create: `SaddleRAG.Tests/ClientIntegration/CopilotCliWriterTests.cs`
- Create: `SaddleRAG.ClientIntegration/Writers/CopilotCliWriter.cs`

**Files (if spike confirmed NO support):**
- Create: `SaddleRAG.Tests/ClientIntegration/CopilotCliWriterTests.cs` (one test asserting stub behavior)
- Create: `SaddleRAG.ClientIntegration/Writers/CopilotCliWriter.cs` (no-op stub)

**Branching note:** the spike output dictates which form this task takes. Two variants below — pick one based on Task 8's findings and follow it through.

#### Variant A: Spike confirmed Copilot CLI MCP support

- [ ] **Step A1: Write fixtures**

Mirror the structure of Task 7 (`vscode-mcp` fixtures), but with the schema documented in Task 8's notes. Five scenarios: `empty`, `other-servers-only`, `existing-saddlerag`, `existing-permissions-allow` (if applicable), `malformed-json`.

- [ ] **Step A2: Write failing tests**

Mirror `VsCodeMcpWriterTests.cs`, swapping client name and config layout.

- [ ] **Step A3: Run tests — verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CopilotCliWriterTests"`

- [ ] **Step A4: Implement `CopilotCliWriter`**

Mirror `VsCodeMcpWriter` structurally; substitute the verified config path and schema. Handle skill drop in `WriteSkillFileAsync` if Task 8 confirmed skill support.

- [ ] **Step A5: Run tests + full suite — verify pass**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CopilotCliWriterTests"
dotnet test SaddleRAG.Tests
```

- [ ] **Step A6: Commit**

Write `msg.txt`:
```
feat(client-integration): add CopilotCliWriter

Writes the GitHub Copilot CLI per-user MCP config (path verified by
Task 8 spike). Mirrors VsCodeMcpWriter behavior: surgical overwrite
of saddlerag key, no-op on missing file, UTF-8 no BOM.
```

#### Variant B: Spike confirmed no Copilot CLI MCP support on Windows

- [ ] **Step B1: Implement no-op stub**

Write `SaddleRAG.ClientIntegration/Writers/CopilotCliWriter.cs`:

```csharp
// CopilotCliWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration.Writers;

public sealed class CopilotCliWriter : IClientWriter
{
    private const string Name = "copilot-cli";
    private const string NotSupportedMessage = "Copilot CLI MCP integration not yet available on this platform — skipped";

    public string ClientName => Name;

    public Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
        => Task.FromResult(RegisterResult.Ok(Name, configPath: string.Empty, NotSupportedMessage));

    public Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
        => Task.FromResult(UnregisterResult.NoOp(Name, configPath: string.Empty, NotSupportedMessage));

    public Task<StatusResult> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new StatusResult(
            ClientName: Name,
            ConfigPath: string.Empty,
            ConfigFileExists: false,
            SaddleRagEntryPresent: false,
            EndpointMatchesCanonical: false,
            SkillFilePresent: null,
            Notes: NotSupportedMessage));
}
```

- [ ] **Step B2: Write tests for stub behavior**

Write `SaddleRAG.Tests/ClientIntegration/CopilotCliWriterTests.cs`:

```csharp
// CopilotCliWriterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class CopilotCliWriterTests
{
    [Fact]
    public async Task RegisterIsNoOpButSucceeds()
    {
        var writer = new CopilotCliWriter();

        var result = await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("not yet available", result.Message);
    }

    [Fact]
    public async Task UnregisterIsNoOpButSucceeds()
    {
        var writer = new CopilotCliWriter();

        var result = await writer.UnregisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.WasNoOp);
    }
}
```

- [ ] **Step B3: Run tests + full suite**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CopilotCliWriterTests"
dotnet test SaddleRAG.Tests
```

- [ ] **Step B4: Commit**

Write `msg.txt`:
```
feat(client-integration): add CopilotCliWriter as no-op stub

Spike (docs/superpowers/notes/copilot-cli-config.md) confirmed
Copilot CLI does not currently expose a stable MCP config path on
Windows. Stub writer no-ops both register and unregister with a
"not yet available" message; dialog checkbox will be disabled.

Replace with a real implementation when Copilot CLI ships MCP
config support — the IClientWriter contract makes the swap
mechanical.
```

---

## Phase 3: Orchestration & CLI

### Task 10: `ClientRegistrar` orchestration + tests

**Files:**
- Create: `SaddleRAG.Tests/ClientIntegration/ClientRegistrarTests.cs`
- Create: `SaddleRAG.ClientIntegration/ClientRegistrar.cs`

- [ ] **Step 1: Write failing tests**

Write `SaddleRAG.Tests/ClientIntegration/ClientRegistrarTests.cs`:

```csharp
// ClientRegistrarTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class ClientRegistrarTests
{
    [Fact]
    public async Task RegisterAllRunsEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar(new IClientWriter[] { w1, w2 });

        var result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.True(result.AllRegisterSucceeded);
        Assert.Equal(2, result.RegisterResults.Count);
        Assert.Equal(1, w1.RegisterCallCount);
        Assert.Equal(1, w2.RegisterCallCount);
    }

    [Fact]
    public async Task RegisterFailureDoesNotStopOtherWriters()
    {
        var w1 = new FakeWriter("alpha", succeed: false);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar(new IClientWriter[] { w1, w2 });

        var result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);

        Assert.False(result.AllRegisterSucceeded);
        Assert.False(result.RegisterResults[0].Success);
        Assert.True(result.RegisterResults[1].Success);
        Assert.Equal(1, w2.RegisterCallCount);
    }

    [Fact]
    public async Task UnregisterAllRunsEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var w2 = new FakeWriter("bravo", succeed: true);
        var registrar = new ClientRegistrar(new IClientWriter[] { w1, w2 });

        var result = await registrar.UnregisterAsync(CancellationToken.None);

        Assert.True(result.AllUnregisterSucceeded);
        Assert.Equal(2, result.UnregisterResults.Count);
    }

    [Fact]
    public async Task RegisterPassesEndpointToEveryWriter()
    {
        var w1 = new FakeWriter("alpha", succeed: true);
        var endpoint = new SaddleRagEndpoint("http://test:1234/mcp", 60, Array.Empty<string>());
        var registrar = new ClientRegistrar(new IClientWriter[] { w1 });

        await registrar.RegisterAsync(endpoint, CancellationToken.None);

        Assert.Equal(endpoint, w1.LastEndpoint);
    }

    private sealed class FakeWriter : IClientWriter
    {
        private readonly bool mSucceed;

        public FakeWriter(string name, bool succeed)
        {
            ClientName = name;
            mSucceed = succeed;
        }

        public string ClientName { get; }
        public int RegisterCallCount { get; private set; }
        public SaddleRagEndpoint? LastEndpoint { get; private set; }

        public Task<RegisterResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
        {
            RegisterCallCount++;
            LastEndpoint = endpoint;
            RegisterResult res = mSucceed
                                     ? RegisterResult.Ok(ClientName, "fake-path", "ok")
                                     : RegisterResult.Failed(ClientName, "fake-path", "boom");
            return Task.FromResult(res);
        }

        public Task<UnregisterResult> UnregisterAsync(CancellationToken ct)
            => Task.FromResult(mSucceed
                                   ? UnregisterResult.Removed(ClientName, "fake-path", "ok")
                                   : UnregisterResult.Failed(ClientName, "fake-path", "boom"));

        public Task<StatusResult> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new StatusResult(ClientName, "fake-path", false, false, false, null, "fake"));
    }
}
```

- [ ] **Step 2: Run tests — confirm failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClientRegistrarTests"`
Expected: build error — `ClientRegistrar` not found.

- [ ] **Step 3: Implement `ClientRegistrar`**

Write `SaddleRAG.ClientIntegration/ClientRegistrar.cs`:

```csharp
// ClientRegistrar.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.ClientIntegration;

public sealed class ClientRegistrar
{
    private readonly IReadOnlyList<IClientWriter> mWriters;

    public ClientRegistrar(IEnumerable<IClientWriter> writers)
    {
        mWriters = writers.ToList();
    }

    public async Task<RegistrarResult> RegisterAsync(SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        List<RegisterResult> results = new();
        foreach (IClientWriter writer in mWriters)
        {
            RegisterResult result = await SafeRegisterAsync(writer, endpoint, ct);
            results.Add(result);
        }
        return RegistrarResult.ForRegister(results);
    }

    public async Task<RegistrarResult> UnregisterAsync(CancellationToken ct)
    {
        List<UnregisterResult> results = new();
        foreach (IClientWriter writer in mWriters)
        {
            UnregisterResult result = await SafeUnregisterAsync(writer, ct);
            results.Add(result);
        }
        return RegistrarResult.ForUnregister(results);
    }

    public async Task<IReadOnlyList<StatusResult>> GetStatusAsync(CancellationToken ct)
    {
        List<StatusResult> results = new();
        foreach (IClientWriter writer in mWriters)
        {
            StatusResult result = await writer.GetStatusAsync(ct);
            results.Add(result);
        }
        return results;
    }

    private static async Task<RegisterResult> SafeRegisterAsync(IClientWriter writer, SaddleRagEndpoint endpoint, CancellationToken ct)
    {
        RegisterResult res;
        try
        {
            res = await writer.RegisterAsync(endpoint, ct);
        }
        catch (Exception ex)
        {
            res = RegisterResult.Failed(writer.ClientName, configPath: string.Empty, $"unhandled exception: {ex.Message}");
        }
        return res;
    }

    private static async Task<UnregisterResult> SafeUnregisterAsync(IClientWriter writer, CancellationToken ct)
    {
        UnregisterResult res;
        try
        {
            res = await writer.UnregisterAsync(ct);
        }
        catch (Exception ex)
        {
            res = UnregisterResult.Failed(writer.ClientName, configPath: string.Empty, $"unhandled exception: {ex.Message}");
        }
        return res;
    }
}
```

- [ ] **Step 4: Run tests — verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ClientRegistrarTests"`
Expected: 4 tests pass.

- [ ] **Step 5: Run full suite**

Run: `dotnet test SaddleRAG.Tests`
Expected: all green.

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(client-integration): add ClientRegistrar orchestrator

Iterates writers in registration order; per-writer failure does
not stop the rest. Wraps each writer call in a guard so an
unhandled exception in one writer surfaces as a per-result failure
instead of poisoning the orchestrator.
```

Run:
```
git add SaddleRAG.Tests/ClientIntegration/ClientRegistrarTests.cs SaddleRAG.ClientIntegration/ClientRegistrar.cs
git commit -F msg.txt
```

---

### Task 11: Round-trip tests across all writers

**Files:**
- Create: `SaddleRAG.Tests/ClientIntegration/RoundTripTests.cs`

The "register then unregister leaves the file byte-identical to its original state" guarantee from the spec. One parameterized test that exercises every writer against every applicable fixture.

- [ ] **Step 1: Write the round-trip test**

Write `SaddleRAG.Tests/ClientIntegration/RoundTripTests.cs`:

```csharp
// RoundTripTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class RoundTripTests : IDisposable
{
    private readonly string mTempDir;

    public RoundTripTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), "saddlerag-rt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mTempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
        {
            Directory.Delete(mTempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("claude-code", "no-mcp-section")]
    [InlineData("claude-code", "other-servers-only")]
    [InlineData("claude-code", "existing-permissions-allow")]
    [InlineData("claude-desktop", "other-servers-only")]
    [InlineData("vscode-mcp", "other-servers-only")]
    public async Task RegisterThenUnregisterRestoresOriginalContent(string client, string scenario)
    {
        string fixtureInput = TestPaths.FixtureFile(client, scenario, "input.json");
        string configPath = Path.Combine(mTempDir, $"{client}-{scenario}.json");
        File.Copy(fixtureInput, configPath);
        string originalText = await File.ReadAllTextAsync(configPath);

        IClientWriter writer = CreateWriter(client, configPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);
        await writer.UnregisterAsync(CancellationToken.None);

        string finalText = await File.ReadAllTextAsync(configPath);
        Assert.Equal(NormalizeJson(originalText), NormalizeJson(finalText));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("claude-desktop")]
    [InlineData("vscode-mcp")]
    public async Task RegisterThenUnregisterFromAbsentFileLeavesFileAbsent(string client)
    {
        string configPath = Path.Combine(mTempDir, $"{client}-absent.json");
        Assert.False(File.Exists(configPath));

        IClientWriter writer = CreateWriter(client, configPath);
        await writer.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);
        Assert.True(File.Exists(configPath), "register should create the file");

        await writer.UnregisterAsync(CancellationToken.None);

        // After unregister, the file may exist as `{}` or be removed; both are acceptable
        // per the spec ("leave the empty object, do not tidy further"). Assert no saddlerag remains.
        if (File.Exists(configPath))
        {
            string finalText = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("saddlerag", finalText);
        }
    }

    private IClientWriter CreateWriter(string client, string configPath)
    {
        IClientWriter res = client switch
                                {
                                    "claude-code" => new ClaudeCodeWriter(
                                        configPath,
                                        Path.Combine(mTempDir, "skills", $"{Path.GetFileNameWithoutExtension(configPath)}.md")),
                                    "claude-desktop" => new ClaudeDesktopWriter(configPath),
                                    "vscode-mcp" => new VsCodeMcpWriter(configPath),
                                    _ => throw new ArgumentException($"unknown client: {client}", nameof(client))
                                };
        return res;
    }

    private static string NormalizeJson(string raw)
    {
        return raw.Replace("\r\n", "\n").Trim();
    }
}
```

- [ ] **Step 2: Run tests — verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RoundTripTests"`
Expected: 8 tests pass.

- [ ] **Step 3: Run full suite**

Run: `dotnet test SaddleRAG.Tests`
Expected: all green.

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
test(client-integration): add register/unregister round-trip tests

Per writer + fixture combination: copy fixture in, register, then
unregister, assert the resulting file is byte-equivalent to the
original. Codifies the spec's "install cleans up after itself"
guarantee as a test rather than a manual check.
```

Run:
```
git add SaddleRAG.Tests/ClientIntegration/RoundTripTests.cs
git commit -F msg.txt
```

---

### Task 12: `SaddleRAG.Cli register-clients` subcommand

**Files:**
- Modify: `SaddleRAG.Cli/SaddleRAG.Cli.csproj`
- Create: `SaddleRAG.Cli/Commands/RegisterClientsCommand.cs`
- Create: `SaddleRAG.Cli/Commands/ClientFlagParser.cs`
- Modify: `SaddleRAG.Cli/Program.cs`
- Create: `SaddleRAG.Tests/ClientIntegration/Cli/RegisterClientsCommandTests.cs`

- [ ] **Step 1: Add the project reference to `SaddleRAG.Cli`**

Modify `SaddleRAG.Cli/SaddleRAG.Cli.csproj`. Find:
```xml
  <ItemGroup>
    <ProjectReference Include="..\SaddleRAG.Core\SaddleRAG.Core.csproj" />
    <ProjectReference Include="..\SaddleRAG.Database\SaddleRAG.Database.csproj" />
    <ProjectReference Include="..\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj" />
  </ItemGroup>
```

Replace with:
```xml
  <ItemGroup>
    <ProjectReference Include="..\SaddleRAG.ClientIntegration\SaddleRAG.ClientIntegration.csproj" />
    <ProjectReference Include="..\SaddleRAG.Core\SaddleRAG.Core.csproj" />
    <ProjectReference Include="..\SaddleRAG.Database\SaddleRAG.Database.csproj" />
    <ProjectReference Include="..\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the `ClientFlagParser` helper**

Write `SaddleRAG.Cli/Commands/ClientFlagParser.cs`:

```csharp
// ClientFlagParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Cli.Commands;

internal static class ClientFlagParser
{
    public static IEnumerable<IClientWriter> SelectWritersForCurrentUser(
        bool claudeCode,
        bool claudeDesktop,
        bool vscodeMcp,
        bool copilotCli)
    {
        if (claudeCode)
        {
            yield return ClaudeCodeWriter.ForCurrentUser();
        }
        if (claudeDesktop)
        {
            yield return ClaudeDesktopWriter.ForCurrentUser();
        }
        if (vscodeMcp)
        {
            yield return VsCodeMcpWriter.ForCurrentUser();
        }
        if (copilotCli)
        {
            yield return new CopilotCliWriter();
        }
    }
}
```

(Replace the `new CopilotCliWriter()` line with whatever construction the Task 9 variant produced — `CopilotCliWriter.ForCurrentUser()` if Variant A, parameterless if Variant B.)

- [ ] **Step 3: Write the `RegisterClientsCommand`**

Write `SaddleRAG.Cli/Commands/RegisterClientsCommand.cs`:

```csharp
// RegisterClientsCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class RegisterClientsCommand
{
    public static Command Build()
    {
        Option<bool> claudeCode    = new("--claude-code")    { Description = "Register with Claude Code (~/.claude.json + skill)", DefaultValueFactory = _ => true };
        Option<bool> claudeDesktop = new("--claude-desktop") { Description = "Register with Claude Desktop",                       DefaultValueFactory = _ => true };
        Option<bool> vscodeMcp     = new("--vscode-mcp")     { Description = "Register with VSCode native MCP",                    DefaultValueFactory = _ => true };
        Option<bool> copilotCli    = new("--copilot-cli")    { Description = "Register with GitHub Copilot CLI",                   DefaultValueFactory = _ => true };
        Option<bool> quiet         = new("--quiet")          { Description = "Suppress per-writer stdout lines",                   DefaultValueFactory = _ => false };
        Option<string?> logFile    = new("--log-file")       { Description = "Append per-writer results to this file" };

        Command cmd = new("register-clients", "Register the SaddleRAG MCP server (and skill where applicable) in supported AI tools' per-user config");
        cmd.Options.Add(claudeCode);
        cmd.Options.Add(claudeDesktop);
        cmd.Options.Add(vscodeMcp);
        cmd.Options.Add(copilotCli);
        cmd.Options.Add(quiet);
        cmd.Options.Add(logFile);

        cmd.SetAction(async (parseResult, ct) =>
        {
            bool cc = parseResult.GetValue(claudeCode);
            bool cd = parseResult.GetValue(claudeDesktop);
            bool vs = parseResult.GetValue(vscodeMcp);
            bool co = parseResult.GetValue(copilotCli);
            bool q = parseResult.GetValue(quiet);
            string? log = parseResult.GetValue(logFile);

            var writers = ClientFlagParser.SelectWritersForCurrentUser(cc, cd, vs, co);
            var registrar = new ClientRegistrar(writers);
            var result = await registrar.RegisterAsync(SaddleRagEndpoint.Default, ct);

            int exitCode = result.AllRegisterSucceeded ? 0 : 2;

            foreach (RegisterResult r in result.RegisterResults)
            {
                string line = $"{r.ClientName,-16} {(r.Success ? "OK " : "ERR")} {r.ConfigPath} — {r.Message}";
                if (!q)
                {
                    Console.WriteLine(line);
                }
                if (log is not null)
                {
                    File.AppendAllText(log, line + Environment.NewLine);
                }
            }

            return exitCode;
        });

        return cmd;
    }
}
```

- [ ] **Step 4: Wire the new command into `Program.cs`**

Read the current end of `SaddleRAG.Cli/Program.cs` to find the root command registration. Add to the `using` block:
```csharp
using SaddleRAG.Cli.Commands;
```

Find the line that creates `RootCommand` (search for `new RootCommand`) and the lines that add subcommands to it. Add immediately after the existing subcommands:
```csharp
rootCommand.Subcommands.Add(RegisterClientsCommand.Build());
```

(Adjust variable name to match what the existing code uses — could be `rootCommand`, `root`, etc.)

- [ ] **Step 5: Write tests**

Write `SaddleRAG.Tests/ClientIntegration/Cli/RegisterClientsCommandTests.cs`:

```csharp
// RegisterClientsCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class RegisterClientsCommandTests
{
    [Fact]
    public void BuildExposesAllExpectedOptions()
    {
        Command cmd = RegisterClientsCommand.Build();

        IReadOnlyCollection<string> expected = new[]
        {
            "--claude-code",
            "--claude-desktop",
            "--vscode-mcp",
            "--copilot-cli",
            "--quiet",
            "--log-file"
        };

        foreach (string opt in expected)
        {
            Assert.Contains(cmd.Options, o => o.Name == opt || o.Aliases.Contains(opt));
        }
    }

    [Fact]
    public void CommandNameIsRegisterClients()
    {
        Command cmd = RegisterClientsCommand.Build();
        Assert.Equal("register-clients", cmd.Name);
    }
}
```

(End-to-end exercise of register/unregister with real file writes lives in Task 15.)

- [ ] **Step 6: Build and run tests**

```
dotnet build SaddleRAG.slnx
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RegisterClientsCommandTests"
dotnet test SaddleRAG.Tests
```

Expected: build succeeds, RegisterClientsCommandTests pass, full suite green.

- [ ] **Step 7: Smoke-test the CLI surface manually**

```
dotnet run --project SaddleRAG.Cli -- register-clients --help
```
Expected: command help shows all six options with their descriptions and defaults.

- [ ] **Step 8: Commit**

Write `msg.txt`:
```
feat(cli): add register-clients subcommand

Wraps ClientRegistrar behind a System.CommandLine subcommand:
per-tool boolean flags (default true), --quiet, --log-file. Exit
codes: 0 all-good, 2 at-least-one-failed, 1 invocation error.
The MSI's RegisterAiClients custom action invokes this in Task 17.
```

Run:
```
git add SaddleRAG.Cli/SaddleRAG.Cli.csproj SaddleRAG.Cli/Commands/ SaddleRAG.Cli/Program.cs SaddleRAG.Tests/ClientIntegration/Cli/
git commit -F msg.txt
```

---

### Task 13: `SaddleRAG.Cli unregister-clients` subcommand

**Files:**
- Create: `SaddleRAG.Cli/Commands/UnregisterClientsCommand.cs`
- Modify: `SaddleRAG.Cli/Program.cs`
- Create: `SaddleRAG.Tests/ClientIntegration/Cli/UnregisterClientsCommandTests.cs`

- [ ] **Step 1: Write the command**

Write `SaddleRAG.Cli/Commands/UnregisterClientsCommand.cs`:

```csharp
// UnregisterClientsCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class UnregisterClientsCommand
{
    public static Command Build()
    {
        Option<bool> claudeCode    = new("--claude-code")    { Description = "Unregister from Claude Code",       DefaultValueFactory = _ => true };
        Option<bool> claudeDesktop = new("--claude-desktop") { Description = "Unregister from Claude Desktop",    DefaultValueFactory = _ => true };
        Option<bool> vscodeMcp     = new("--vscode-mcp")     { Description = "Unregister from VSCode native MCP", DefaultValueFactory = _ => true };
        Option<bool> copilotCli    = new("--copilot-cli")    { Description = "Unregister from Copilot CLI",       DefaultValueFactory = _ => true };
        Option<bool> quiet         = new("--quiet")          { Description = "Suppress per-writer stdout lines",  DefaultValueFactory = _ => false };
        Option<string?> logFile    = new("--log-file")       { Description = "Append per-writer results to this file" };

        Command cmd = new("unregister-clients", "Remove SaddleRAG from supported AI tools' per-user config");
        cmd.Options.Add(claudeCode);
        cmd.Options.Add(claudeDesktop);
        cmd.Options.Add(vscodeMcp);
        cmd.Options.Add(copilotCli);
        cmd.Options.Add(quiet);
        cmd.Options.Add(logFile);

        cmd.SetAction(async (parseResult, ct) =>
        {
            bool cc = parseResult.GetValue(claudeCode);
            bool cd = parseResult.GetValue(claudeDesktop);
            bool vs = parseResult.GetValue(vscodeMcp);
            bool co = parseResult.GetValue(copilotCli);
            bool q = parseResult.GetValue(quiet);
            string? log = parseResult.GetValue(logFile);

            var writers = ClientFlagParser.SelectWritersForCurrentUser(cc, cd, vs, co);
            var registrar = new ClientRegistrar(writers);
            var result = await registrar.UnregisterAsync(ct);

            int exitCode = result.AllUnregisterSucceeded ? 0 : 2;

            foreach (UnregisterResult r in result.UnregisterResults)
            {
                string status = r.Success ? (r.WasNoOp ? "NOOP" : "OK  ") : "ERR ";
                string line = $"{r.ClientName,-16} {status} {r.ConfigPath} — {r.Message}";
                if (!q)
                {
                    Console.WriteLine(line);
                }
                if (log is not null)
                {
                    File.AppendAllText(log, line + Environment.NewLine);
                }
            }

            return exitCode;
        });

        return cmd;
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

Add to the root command's subcommands, immediately after `RegisterClientsCommand.Build()`:
```csharp
rootCommand.Subcommands.Add(UnregisterClientsCommand.Build());
```

- [ ] **Step 3: Write tests**

Write `SaddleRAG.Tests/ClientIntegration/Cli/UnregisterClientsCommandTests.cs`:

```csharp
// UnregisterClientsCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class UnregisterClientsCommandTests
{
    [Fact]
    public void BuildExposesAllExpectedOptions()
    {
        Command cmd = UnregisterClientsCommand.Build();

        IReadOnlyCollection<string> expected = new[]
        {
            "--claude-code",
            "--claude-desktop",
            "--vscode-mcp",
            "--copilot-cli",
            "--quiet",
            "--log-file"
        };

        foreach (string opt in expected)
        {
            Assert.Contains(cmd.Options, o => o.Name == opt || o.Aliases.Contains(opt));
        }
    }

    [Fact]
    public void CommandNameIsUnregisterClients()
    {
        Command cmd = UnregisterClientsCommand.Build();
        Assert.Equal("unregister-clients", cmd.Name);
    }
}
```

- [ ] **Step 4: Build, test, smoke**

```
dotnet build SaddleRAG.slnx
dotnet test SaddleRAG.Tests
dotnet run --project SaddleRAG.Cli -- unregister-clients --help
```

Expected: build green, tests pass, help text appears.

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(cli): add unregister-clients subcommand

Mirror of register-clients with the inverse semantics: per-tool
flags select which writers to invoke, results print one line per
writer, exit code 0 on success (including no-op uninstall of a
clean machine per the spec).
```

Run:
```
git add SaddleRAG.Cli/Commands/UnregisterClientsCommand.cs SaddleRAG.Cli/Program.cs SaddleRAG.Tests/ClientIntegration/Cli/UnregisterClientsCommandTests.cs
git commit -F msg.txt
```

---

### Task 14: `SaddleRAG.Cli status` subcommand

**Files:**
- Create: `SaddleRAG.Cli/Commands/StatusCommand.cs`
- Modify: `SaddleRAG.Cli/Program.cs`
- Create: `SaddleRAG.Tests/ClientIntegration/Cli/StatusCommandTests.cs`

- [ ] **Step 1: Write the command**

Write `SaddleRAG.Cli/Commands/StatusCommand.cs`:

```csharp
// StatusCommand.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using System.Text.Json;
using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;

#endregion

namespace SaddleRAG.Cli.Commands;

public static class StatusCommand
{
    public static Command Build()
    {
        Option<bool> json = new("--json") { Description = "Emit JSON instead of human-readable lines", DefaultValueFactory = _ => false };

        Command cmd = new("status", "Show SaddleRAG registration status across all supported AI tools");
        cmd.Options.Add(json);

        cmd.SetAction(async (parseResult, ct) =>
        {
            bool emitJson = parseResult.GetValue(json);

            var writers = ClientFlagParser.SelectWritersForCurrentUser(
                claudeCode: true, claudeDesktop: true, vscodeMcp: true, copilotCli: true);
            var registrar = new ClientRegistrar(writers);
            IReadOnlyList<StatusResult> results = await registrar.GetStatusAsync(ct);

            if (emitJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                foreach (StatusResult r in results)
                {
                    string mark = r.SaddleRagEntryPresent ? (r.EndpointMatchesCanonical ? "OK " : "OLD") : "—  ";
                    Console.WriteLine($"{r.ClientName,-16} {mark} {r.ConfigPath}");
                    if (!string.IsNullOrEmpty(r.Notes))
                    {
                        Console.WriteLine($"                     {r.Notes}");
                    }
                }
            }

            return 0;
        });

        return cmd;
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

Add:
```csharp
rootCommand.Subcommands.Add(StatusCommand.Build());
```

- [ ] **Step 3: Write tests**

Write `SaddleRAG.Tests/ClientIntegration/Cli/StatusCommandTests.cs`:

```csharp
// StatusCommandTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.CommandLine;
using SaddleRAG.Cli.Commands;

#endregion

namespace SaddleRAG.Tests.ClientIntegration.Cli;

public sealed class StatusCommandTests
{
    [Fact]
    public void CommandNameIsStatus()
    {
        Command cmd = StatusCommand.Build();
        Assert.Equal("status", cmd.Name);
    }

    [Fact]
    public void HasJsonOption()
    {
        Command cmd = StatusCommand.Build();
        Assert.Contains(cmd.Options, o => o.Name == "--json");
    }
}
```

- [ ] **Step 4: Build, test, smoke**

```
dotnet build SaddleRAG.slnx
dotnet test SaddleRAG.Tests
dotnet run --project SaddleRAG.Cli -- status
dotnet run --project SaddleRAG.Cli -- status --json
```

Expected: build green, tests pass, status reads from your real per-user paths and reports four rows.

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(cli): add status subcommand

Read-only diagnostic. Reports per tool: config path, file present,
saddlerag entry present, endpoint matches canonical, skill file
present (where applicable). --json for machine-readable.
```

Run:
```
git add SaddleRAG.Cli/Commands/StatusCommand.cs SaddleRAG.Cli/Program.cs SaddleRAG.Tests/ClientIntegration/Cli/StatusCommandTests.cs
git commit -F msg.txt
```

---

### Task 15: End-to-end "fake-USERPROFILE" test

**Files:**
- Create: `SaddleRAG.Tests/ClientIntegration/EndToEndTests.cs`

The single integration test that drives the full orchestrator (with real writers, fake paths) through register → unregister and asserts no residue. This is the highest-confidence guarantee that the install / uninstall pair work cleanly across all writers.

- [ ] **Step 1: Write the test**

Write `SaddleRAG.Tests/ClientIntegration/EndToEndTests.cs`:

```csharp
// EndToEndTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.ClientIntegration;
using SaddleRAG.ClientIntegration.Models;
using SaddleRAG.ClientIntegration.Writers;

#endregion

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class EndToEndTests : IDisposable
{
    private readonly string mFakeProfile;
    private readonly string mFakeAppData;
    private readonly string mClaudeCodeConfig;
    private readonly string mClaudeCodeSkill;
    private readonly string mClaudeDesktopConfig;
    private readonly string mVsCodeMcp;

    public EndToEndTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "saddlerag-e2e-" + Guid.NewGuid().ToString("N"));
        mFakeProfile = Path.Combine(root, "profile");
        mFakeAppData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(mFakeProfile);
        Directory.CreateDirectory(mFakeAppData);

        mClaudeCodeConfig    = Path.Combine(mFakeProfile, ".claude.json");
        mClaudeCodeSkill     = Path.Combine(mFakeProfile, ".claude", "skills", "saddlerag-first", "SKILL.md");
        mClaudeDesktopConfig = Path.Combine(mFakeAppData, "Claude", "claude_desktop_config.json");
        mVsCodeMcp           = Path.Combine(mFakeAppData, "Code", "User", "mcp.json");
    }

    public void Dispose()
    {
        string root = Path.GetDirectoryName(mFakeProfile)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterAllThenUnregisterAllLeavesEverythingClean()
    {
        var writers = new IClientWriter[]
        {
            new ClaudeCodeWriter(mClaudeCodeConfig, mClaudeCodeSkill),
            new ClaudeDesktopWriter(mClaudeDesktopConfig),
            new VsCodeMcpWriter(mVsCodeMcp)
        };
        var registrar = new ClientRegistrar(writers);

        var registerResult = await registrar.RegisterAsync(SaddleRagEndpoint.Default, CancellationToken.None);
        Assert.True(registerResult.AllRegisterSucceeded,
            string.Join("; ", registerResult.RegisterResults.Where(r => !r.Success).Select(r => r.Message)));

        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig));
        Assert.Contains("saddlerag", await File.ReadAllTextAsync(mVsCodeMcp));
        Assert.True(File.Exists(mClaudeCodeSkill));

        var unregisterResult = await registrar.UnregisterAsync(CancellationToken.None);
        Assert.True(unregisterResult.AllUnregisterSucceeded);

        if (File.Exists(mClaudeCodeConfig))
        {
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeCodeConfig));
        }
        if (File.Exists(mClaudeDesktopConfig))
        {
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mClaudeDesktopConfig));
        }
        if (File.Exists(mVsCodeMcp))
        {
            Assert.DoesNotContain("saddlerag", await File.ReadAllTextAsync(mVsCodeMcp));
        }
        Assert.False(File.Exists(mClaudeCodeSkill));
    }
}
```

- [ ] **Step 2: Run test + full suite**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~EndToEndTests"
dotnet test SaddleRAG.Tests
```

Expected: 1 e2e test passes, full suite green.

- [ ] **Step 3: Commit**

Write `msg.txt`:
```
test(client-integration): end-to-end register/unregister against fake user dirs

One integration test that drives ClientRegistrar with real writers
pointing at temp directories, verifies every config gets the
saddlerag entry, then verifies unregister removes it cleanly. The
install-cleans-up-after-itself guarantee end-to-end in one shot.
```

Run:
```
git add SaddleRAG.Tests/ClientIntegration/EndToEndTests.cs
git commit -F msg.txt
```

---

## Phase 4: MSI integration

### Task 16: Replace `ClaudePluginDlg` with `AiClientsDlg` (four checkboxes)

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`

- [ ] **Step 1: Replace the existing dialog block**

In `Package.wxs`, find the `<Dialog Id="ClaudePluginDlg" ...>` block and replace it entirely with:

```xml
            <!-- AI Tools Integration dialog: four checkboxes, all pre-checked -->
            <Dialog Id="AiClientsDlg" Width="370" Height="270" Title="AI Tools Integration">
                <Control Id="BannerBitmap" Type="Bitmap" X="0" Y="0" Width="370" Height="44" TabSkip="no" Text="WixUI_Bmp_Banner" />
                <Control Id="BannerLine" Type="Line" X="0" Y="44" Width="370" Height="0" />

                <Control Id="Title" Type="Text" X="20" Y="10" Width="290" Height="14"
                         Transparent="yes" NoPrefix="yes"
                         Text="{\WixUI_Font_Title}AI Tools Integration" />

                <Control Id="Description" Type="Text" X="25" Y="55" Width="320" Height="40"
                         Transparent="yes" NoPrefix="yes"
                         Text="SaddleRAG can register itself with these AI tools so its tools and skill are available automatically in every session. All recommended." />

                <Control Id="ChkClaudeCode" Type="CheckBox" X="25" Y="100" Width="320" Height="17"
                         Property="REGISTER_CLAUDE_CODE" CheckBoxValue="1"
                         Text="Claude Code (terminal + VSCode extension)" />
                <Control Id="ChkClaudeDesktop" Type="CheckBox" X="25" Y="120" Width="320" Height="17"
                         Property="REGISTER_CLAUDE_DESKTOP" CheckBoxValue="1"
                         Text="Claude Desktop" />
                <Control Id="ChkVsCodeMcp" Type="CheckBox" X="25" Y="140" Width="320" Height="17"
                         Property="REGISTER_VSCODE_MCP" CheckBoxValue="1"
                         Text="VSCode (Copilot Chat MCP)" />
                <Control Id="ChkCopilotCli" Type="CheckBox" X="25" Y="160" Width="320" Height="17"
                         Property="REGISTER_COPILOT_CLI" CheckBoxValue="1"
                         Text="GitHub Copilot CLI" />

                <Control Id="InfoText" Type="Text" X="25" Y="185" Width="320" Height="35"
                         Transparent="yes" NoPrefix="yes"
                         Text="Touches per-user config files only: %USERPROFILE%\.claude.json, %APPDATA%\Claude\claude_desktop_config.json, %APPDATA%\Code\User\mcp.json, and the Copilot CLI per-user config." />

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

- [ ] **Step 2: Update navigation references**

Find any `<Publish>` element that targets `ClaudePluginDlg` and change `Value="ClaudePluginDlg"` to `Value="AiClientsDlg"`. There should be two: `OllamaDlg`'s Next publish and `VerifyReadyDlg`'s Back publish.

Search to be sure:
```
grep -n ClaudePluginDlg SaddleRAG.Installer/Package.wxs
```
Expected: zero matches after the edits.

- [ ] **Step 3: Verify the WiX build**

```
wix build SaddleRAG.Installer/Package.wxs `
    -d PublishDir=.\artifacts\0.0.0\publish `
    -d Version=0.0.0 `
    -d PluginSourceDir=.\plugin `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

If `artifacts\0.0.0\publish` doesn't exist:
```
dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\0.0.0\publish
```

Expected: build succeeds (the dialog change is purely UI; properties and CAs change in Task 17).

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
feat(installer): replace ClaudePluginDlg with AiClientsDlg

Four checkbox dialog covering all four AI tool registrations
(Claude Code, Claude Desktop, VSCode MCP, Copilot CLI). All
pre-checked. Updates OllamaDlg → AiClientsDlg → VerifyReadyDlg
navigation. Properties and custom actions still wired to the old
single REGISTER_CLAUDE_PLUGIN scheme; Task 17 swaps those.
```

Run:
```
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```

---

### Task 17: Replace `REGISTER_CLAUDE_PLUGIN` property and PS1 CAs

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`

- [ ] **Step 1: Replace the single property with four**

Find:
```xml
        <Property Id="REGISTER_CLAUDE_PLUGIN" Value="1" />
        <Property Id="CLAUDEPLUGINSTATUS" Secure="yes" />
```

Replace with:
```xml
        <Property Id="REGISTER_CLAUDE_CODE"    Value="1" Secure="yes" />
        <Property Id="REGISTER_CLAUDE_DESKTOP" Value="1" Secure="yes" />
        <Property Id="REGISTER_VSCODE_MCP"     Value="1" Secure="yes" />
        <Property Id="REGISTER_COPILOT_CLI"    Value="1" Secure="yes" />
```

- [ ] **Step 2: Replace the RegisterClaudePlugin CA + SetProperty pair**

Find the `<SetProperty Id="RegisterClaudePlugin" ...>` and the matching `<CustomAction Id="RegisterClaudePlugin" ...>`. Replace both with:

```xml
        <!-- Register SaddleRAG with selected AI tools via the SaddleRAG.Cli subcommand. -->
        <!-- Impersonate="yes" so $USERPROFILE / $APPDATA resolve to the installing user. -->
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

- [ ] **Step 3: Replace the UnregisterClaudePlugin CA + SetProperty pair**

Find the matching `UnregisterClaudePlugin` SetProperty + CustomAction. Replace with:

```xml
        <!-- Unregister SaddleRAG from all four AI tools on uninstall. Always runs against -->
        <!-- the full set; per-tool selection at install time does not affect uninstall.   -->
        <SetProperty Id="UnregisterAiClients"
                     Before="UnregisterAiClients"
                     Sequence="execute"
                     Value="&quot;[INSTALLFOLDER]SaddleRAG.Cli.exe&quot; unregister-clients --quiet --log-file &quot;%TEMP%\SaddleRAG-unregister.log&quot;" />

        <CustomAction Id="UnregisterAiClients"
                      BinaryRef="Wix4UtilCA_X86"
                      DllEntry="WixQuietExec"
                      Execute="deferred"
                      Return="ignore"
                      Impersonate="yes" />
```

- [ ] **Step 4: Update the InstallExecuteSequence references**

Find:
```xml
            <Custom Action="RegisterClaudePlugin" After="PatchAppSettings" Condition="(NOT Installed OR REINSTALL) AND REGISTER_CLAUDE_PLUGIN = &quot;1&quot;" />
            <Custom Action="UnregisterClaudePlugin" Before="RemoveFiles" Condition="REMOVE = &quot;ALL&quot;" />
```

Replace with:
```xml
            <Custom Action="RegisterAiClients" After="PatchAppSettings" Condition="NOT Installed OR REINSTALL" />
            <Custom Action="UnregisterAiClients" Before="RemoveFiles" Condition="REMOVE = &quot;ALL&quot;" />
```

(The per-tool gate is no longer needed at the MSI condition — the CLI itself respects the `--claude-code=0` etc. flags.)

- [ ] **Step 5: Verify the WiX build**

```
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

Write `msg.txt`:
```
feat(installer): replace single Claude property + PS1 CAs with four-tool CLI invocation

REGISTER_CLAUDE_PLUGIN becomes REGISTER_CLAUDE_CODE +
REGISTER_CLAUDE_DESKTOP + REGISTER_VSCODE_MCP + REGISTER_COPILOT_CLI,
each defaulting to "1" and bound to a checkbox in AiClientsDlg.

The two PowerShell-script custom actions (Register/Unregister-
ClaudePlugin) are replaced by WixQuietExec calls to
SaddleRAG.Cli.exe register-clients / unregister-clients. The CLI
honors the per-tool flags. Custom actions both Return=ignore so a
registration failure never fails the MSI install.
```

Run:
```
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```

---

### Task 18: Remove the PS1 helper scripts from the MSI payload

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`
- Delete: `SaddleRAG.Installer/RegisterClaudePlugin.ps1`
- Delete: `SaddleRAG.Installer/UnregisterClaudePlugin.ps1`

- [ ] **Step 1: Remove the two `<File>` references from `HelperScripts`**

Find:
```xml
        <Component Id="HelperScripts" Directory="MCPFOLDER" Guid="B3A1F2E4-9C7D-4E8B-A5F1-2D3E4F5A6B7C">
            <File Source="RegisterClaudePlugin.ps1" />
            <File Source="UnregisterClaudePlugin.ps1" />
            <File Source="SetOllamaKeepAlive.ps1" />
            <File Source="UnsetOllamaKeepAlive.ps1" />
        </Component>
```

Replace with:
```xml
        <Component Id="HelperScripts" Directory="MCPFOLDER" Guid="B3A1F2E4-9C7D-4E8B-A5F1-2D3E4F5A6B7C">
            <File Source="SetOllamaKeepAlive.ps1" />
            <File Source="UnsetOllamaKeepAlive.ps1" />
        </Component>
```

- [ ] **Step 2: Delete the two PS1 files**

```
git rm SaddleRAG.Installer/RegisterClaudePlugin.ps1 SaddleRAG.Installer/UnregisterClaudePlugin.ps1
```

- [ ] **Step 3: Verify build**

```
wix build SaddleRAG.Installer/Package.wxs `
    -d PublishDir=.\artifacts\0.0.0\publish `
    -d Version=0.0.0 `
    -d PluginSourceDir=.\plugin `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

Expected: build succeeds; MSI no longer includes the PS1 helpers.

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
chore(installer): remove obsolete RegisterClaudePlugin PS1 helpers

Functionality replaced by SaddleRAG.Cli register-clients /
unregister-clients subcommands invoked via WixQuietExec. The two
remaining helper scripts (SetOllamaKeepAlive / UnsetOllamaKeepAlive)
stay — Ollama integration is unchanged.
```

Run:
```
git add SaddleRAG.Installer/Package.wxs
git commit -F msg.txt
```

(The `git rm` from Step 2 is already staged; this commits both.)

---

## Phase 5: Cleanup

### Task 19: Delete repo `.mcp.json`

**Files:**
- Delete: `.mcp.json`

- [ ] **Step 1: Delete and stage**

```
git rm .mcp.json
```

- [ ] **Step 2: Confirm Claude Code in this repo still works**

Open a new Claude Code session in this repo. Run `claude mcp list` (or whatever the CLI surface is). Expect: `saddlerag` listed via the user-scope `~/.claude.json` registration that the new install will provide. (Until you've re-installed via the new MSI, you may see nothing — that's fine, it confirms the project-scope registration is gone.)

- [ ] **Step 3: Commit**

Write `msg.txt`:
```
chore: remove project-scoped .mcp.json

Global SaddleRAG install (new MSI) wires saddlerag at user scope in
~/.claude.json so a project-scoped .mcp.json is now redundant —
and was the source of the duplicate-registration footgun the
plugin README warned about.
```

Run:
```
git commit -F msg.txt
```

---

### Task 20: Delete repo `plugin/` folder

**Files:**
- Delete: `plugin/` (whole tree)

- [ ] **Step 1: Confirm the skill content is in the embedded resource**

Run:
```
diff plugin/skills/saddlerag-first/SKILL.md SaddleRAG.ClientIntegration/Resources/saddlerag-first.md
```
Expected: no diff (Task 4 copied byte-for-byte).

- [ ] **Step 2: Delete the folder**

```
git rm -r plugin
```

- [ ] **Step 3: Verify build still passes**

```
dotnet build SaddleRAG.slnx
dotnet test SaddleRAG.Tests
```

Expected: build green, tests green. (The MSI's `wix build` no longer needs `-d PluginSourceDir=`; `PluginFiles` ComponentGroup remains in `Package.wxs` for now and would error on the missing `$(var.PluginSourceDir)`. Task 21 deals with that.)

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
chore: remove plugin/ folder from repo

The skill content moved to SaddleRAG.ClientIntegration/Resources/
saddlerag-first.md (embedded resource) in an earlier task. The
folder's .mcp.json and plugin.json were dead artifacts of the old
"Claude Code plugin via files" scheme — superseded by the user-
scope registration the new MSI / CLI now performs.
```

Run:
```
git commit -F msg.txt
```

---

### Task 21: Remove `PluginFiles` ComponentGroup and `PluginSourceDir` from MSI

**Files:**
- Modify: `SaddleRAG.Installer/Package.wxs`
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Remove `PluginFiles` ComponentGroup and `PLUGINFOLDER` directory**

In `Package.wxs`, find:
```xml
        <StandardDirectory Id="ProgramFiles64Folder">
            <Directory Id="MCPFOLDER" Name="SaddleRAG.Mcp">
                <Directory Id="PLUGINFOLDER" Name="plugin" />
            </Directory>
        </StandardDirectory>
```

Replace with:
```xml
        <StandardDirectory Id="ProgramFiles64Folder">
            <Directory Id="MCPFOLDER" Name="SaddleRAG.Mcp" />
        </StandardDirectory>
```

Find and delete:
```xml
        <!-- Plugin files (always installed; registration is opt-in via dialog) -->
        <ComponentGroup Id="PluginFiles" Directory="PLUGINFOLDER">
            <Files Include="$(var.PluginSourceDir)\**" />
        </ComponentGroup>
```

Find:
```xml
        <Feature Id="Main" Title="SaddleRAG MCP Server" Level="1">
            <ComponentGroupRef Id="PublishOutput" />
            <ComponentGroupRef Id="PluginFiles" />
            <ComponentRef Id="ServiceComponent" />
            <ComponentRef Id="HelperScripts" />
        </Feature>
```

Replace with:
```xml
        <Feature Id="Main" Title="SaddleRAG MCP Server" Level="1">
            <ComponentGroupRef Id="PublishOutput" />
            <ComponentRef Id="ServiceComponent" />
            <ComponentRef Id="HelperScripts" />
        </Feature>
```

- [ ] **Step 2: Remove `-d PluginSourceDir=` from CI**

Modify `.github/workflows/build.yml`. Find the `wix build` line and remove the `-d PluginSourceDir=${{ github.workspace }}/plugin` argument.

- [ ] **Step 3: Verify the WiX build**

```
wix build SaddleRAG.Installer/Package.wxs `
    -d PublishDir=.\artifacts\0.0.0\publish `
    -d Version=0.0.0 `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -o .\artifacts\0.0.0\SaddleRAG.Mcp.msi
```

Expected: build succeeds without `-d PluginSourceDir=`.

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
chore(installer): drop PluginFiles component and PluginSourceDir

Plugin folder is gone from the repo (skill content moved to
embedded resource, MCP wiring now done by SaddleRAG.Cli). The
PLUGINFOLDER directory and PluginFiles ComponentGroup that copied
plugin/ into the install dir are no longer needed; CI wix build
no longer needs PluginSourceDir.
```

Run:
```
git add SaddleRAG.Installer/Package.wxs .github/workflows/build.yml
git commit -F msg.txt
```

---

### Task 22: Update README — install + troubleshooting

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Read the current README sections that mention the plugin or .mcp.json**

```
grep -n "plugin\|mcp.json\|RegisterClaudePlugin\|saddlerag-first" README.md
```

Note every section that references the old install path.

- [ ] **Step 2: Update the install section**

In `README.md`'s install / quick-start section, replace any text that says "install the plugin via `claude --plugin-dir ...`" with: "Run the MSI installer — it registers SaddleRAG with all your installed AI tools automatically." Mention the four supported tools.

- [ ] **Step 3: Update the troubleshooting section**

Add (or update) entries for:
- "SaddleRAG isn't visible in Claude Code / Desktop / VSCode / Copilot." Recovery: `SaddleRAG.Cli status` then `SaddleRAG.Cli register-clients`.
- "I want to disable SaddleRAG for one tool." Use `SaddleRAG.Cli unregister-clients --claude-desktop=true --claude-code=false ...`.
- Drop the old "stale `NODE_EXTRA_CA_CERTS` setting" guidance only if it's no longer relevant (carry it over from `plugin/README.md` if useful).

- [ ] **Step 4: Verify README renders cleanly**

Open `README.md` in a Markdown preview (VSCode `Ctrl+Shift+V` works). Verify links resolve, headings are sensible, no broken references to deleted `plugin/` paths.

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
docs(readme): document the new MSI-driven AI clients integration

Install section: MSI auto-wires Claude Code, Claude Desktop, VSCode
MCP, and Copilot CLI. Troubleshooting section: SaddleRAG.Cli status /
register-clients / unregister-clients for diagnosis and per-tool
control.
```

Run:
```
git add README.md
git commit -F msg.txt
```

---

## Phase 6: Smoke test

### Task 23: Manual MSI install / repair / uninstall smoke test

No automated harness exists for MSI behavior. Verify by running the MSI on this dev machine.

**Prerequisites:** MongoDB on 27017, Ollama on 11434, a built `SaddleRAG.Mcp.msi` from Task 21.

- [ ] **Step 1: Manual cleanup of legacy state on this machine**

Per the spec's "One-time hand cleanup" section:
```
claude plugin uninstall saddlerag
```

If that errors on the dangling entry, hand-edit `%USERPROFILE%\.claude\plugins\installed_plugins.json` and remove the `"saddlerag@local"` array entry.

Also remove the legacy wrong-file entry from the previous MSI scheme:
```
$f = "$env:USERPROFILE\.claude\settings.json"
if (Test-Path $f) {
    $j = Get-Content $f -Raw | ConvertFrom-Json
    if ($j.PSObject.Properties['mcpServers'] -and $j.mcpServers.PSObject.Properties['saddlerag']) {
        $j.mcpServers.PSObject.Properties.Remove('saddlerag')
        $j | ConvertTo-Json -Depth 20 | Set-Content $f -Encoding UTF8
    }
}
```

Verify:
```
Test-Path $env:USERPROFILE\.claude\plugins\saddlerag
```
Expected: `False`.

- [ ] **Step 2: Fresh install — happy path, all checkboxes left checked**

1. Run `SaddleRAG.Mcp.msi` as admin.
2. Click through dialogs. On "AI Tools Integration", confirm all four checkboxes are checked. Complete the install.
3. Verify each registration:

```
SaddleRAG.Cli status
```
Expected: four rows, each with `OK` and a path. The `endpointMatchesCanonical` flag is true on every row.

4. Spot-check the actual files:
```
(Get-Content "$env:USERPROFILE\.claude.json" -Raw | ConvertFrom-Json).mcpServers.saddlerag
(Get-Content "$env:APPDATA\Claude\claude_desktop_config.json" -Raw | ConvertFrom-Json).mcpServers.saddlerag
(Get-Content "$env:APPDATA\Code\User\mcp.json" -Raw | ConvertFrom-Json).servers.saddlerag
```
Expected: each shows the canonical entry from the spec.

5. Verify the skill file landed:
```
Test-Path "$env:USERPROFILE\.claude\skills\saddlerag-first\SKILL.md"
```
Expected: `True`.

6. Open Claude Code in any directory other than this repo. Ask "use list_libraries". Expected: Claude Code calls `mcp__saddlerag__list_libraries` without any project-scope `.mcp.json`.

- [ ] **Step 3: Repair install**

```
msiexec /fa SaddleRAG.Mcp.msi
```
Then re-run the file checks from Step 2.4. Expected: every `saddlerag` entry is still present and canonical (idempotent overwrite is harmless).

- [ ] **Step 4: Install with a checkbox unchecked**

1. Uninstall via Programs and Features.
2. Re-run the MSI; on the AI Tools Integration dialog, uncheck "GitHub Copilot CLI". Complete install.
3. Run `SaddleRAG.Cli status`. Expected: three rows OK, one row says "not registered" (or the spike's "not yet available" if Variant B from Task 9).

- [ ] **Step 5: Uninstall**

1. Uninstall via `msiexec /x SaddleRAG.Mcp.msi`.
2. Verify each config file no longer contains `saddlerag` (other entries untouched):
```
(Get-Content "$env:USERPROFILE\.claude.json" -Raw | ConvertFrom-Json).mcpServers
(Get-Content "$env:APPDATA\Claude\claude_desktop_config.json" -Raw | ConvertFrom-Json).mcpServers
(Get-Content "$env:APPDATA\Code\User\mcp.json" -Raw | ConvertFrom-Json).servers
```
Expected: no `saddlerag` key in any of the three; pre-existing siblings (e.g., `azure-devops`, `azure`, `filesystem`) intact.
3. Verify skill file removed:
```
Test-Path "$env:USERPROFILE\.claude\skills\saddlerag-first\SKILL.md"
```
Expected: `False`.

- [ ] **Step 6: Smoke any fixes**

If any test in Steps 2–5 surfaced a bug, fix in code, rebuild MSI, re-run from Step 1. Commit fixes:

Write `msg.txt`:
```
fix(<area>): <what was broken in smoke test>
```
Run:
```
git add <files>
git commit -F msg.txt
```

- [ ] **Step 7: Push the branch and open the PR**

```
git push -u origin claude/ai-clients-integration
gh pr create --title "AI clients integration: unified MSI registration for Claude Code / Desktop / VSCode MCP / Copilot CLI" --body-file pr-body.md
```

Write `pr-body.md` with a summary, link to the spec doc and plan doc, and the smoke-test checklist results from Steps 2–5.

---

## Self-Review

(Performed at plan-write time; preserved here for the executing engineer's reference.)

**Spec coverage:**
- Goals 1–7 from the spec → Tasks 5–9 (writers) + 12–14 (CLI) + 16–17 (MSI) + 23 (smoke). ✓
- Non-goals (multi-user, pre-install scrub, watcher, repo-wide `.mcp.json` removal, cross-platform MSI) → not addressed in any task. ✓
- Architecture sections (module layout, contract, behavior rules, orchestration, CLI surface, MSI integration) → Tasks 1–17 implement each piece. ✓
- Testing layers 1–5 from the spec → Tasks 5–11, 12–14, 15. ✓
- One-time hand cleanup → Tasks 19, 20, 23 Step 1. ✓
- Compatibility / migration / risks → addressed via test coverage and Task 23 explicit verifications.

**Placeholder scan:** No "TBD", "TODO", "implement later" in any task body. The Copilot CLI Variant A/B branching in Task 9 is by design — pick one based on Task 8's findings. The CLI flag list in Task 12 Step 4 says "adjust variable name to match what the existing code uses" — that's a one-token fix-up, not a placeholder.

**Type consistency:** `IClientWriter` method signatures match between the contract (Task 3), the writers (Tasks 5–9), the orchestrator (Task 10), and the CLI (Tasks 12–14). `SaddleRagEndpoint`, `RegisterResult`, `UnregisterResult`, `StatusResult`, `RegistrarResult` are referenced consistently. Property names in the WiX (Tasks 16, 17) match the CLI flag names in `RegisterClientsCommand` (Task 12).
