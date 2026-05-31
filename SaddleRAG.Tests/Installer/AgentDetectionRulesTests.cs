// AgentDetectionRulesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using SaddleRAG.Installer.Logic;
using Xunit;

namespace SaddleRAG.Tests.Installer
{
    public sealed class AgentDetectionRulesTests : IDisposable
    {
        private readonly string mTempDir;

        public AgentDetectionRulesTests()
        {
            mTempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(mTempDir);
            Directory.CreateDirectory(UserProfile);
            Directory.CreateDirectory(AppData);
            Directory.CreateDirectory(ProgramFiles);
        }

        public void Dispose()
        {
            if (Directory.Exists(mTempDir))
            {
                Directory.Delete(mTempDir, recursive: true);
            }
        }

        private string UserProfile => Path.Combine(mTempDir, "profile");

        private string AppData => Path.Combine(mTempDir, "appdata");

        private string ProgramFiles => Path.Combine(mTempDir, "programfiles");

        private IReadOnlyList<string> Detect()
        {
            return AgentDetectionRules.DetectInstalledAgents(UserProfile, AppData, ProgramFiles);
        }

        [Fact]
        public void EmptyRootsReturnsEmpty()
        {
            Assert.Empty(Detect());
        }

        [Fact]
        public void CursorDirDetectsOnlyCursor()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".cursor"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "cursor" }, result);
        }

        [Fact]
        public void ClaudeJsonFileDetectsClaudeCode()
        {
            File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), "{}");
            Assert.Contains("claude-code", Detect());
        }

        [Fact]
        public void ClaudeDirDetectsClaudeCode()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".claude"));
            Assert.Contains("claude-code", Detect());
        }

        [Fact]
        public void ClaudeAppDataDirDetectsClaudeDesktop()
        {
            Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "claude-desktop" }, result);
        }

        [Fact]
        public void CodeAppDataDirDetectsVsCode()
        {
            Directory.CreateDirectory(Path.Combine(AppData, "Code"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "vscode-mcp" }, result);
        }

        [Fact]
        public void CopilotDirDetectsCopilotCli()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".copilot"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "copilot-cli" }, result);
        }

        [Fact]
        public void CodexDirDetectsCodex()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".codex"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "codex" }, result);
        }

        [Fact]
        public void GeminiDirDetectsGeminiCli()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".gemini"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "gemini-cli" }, result);
        }

        [Fact]
        public void CodeiumWindsurfDirDetectsWindsurf()
        {
            Directory.CreateDirectory(Path.Combine(UserProfile, ".codeium", "windsurf"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "windsurf" }, result);
        }

        [Fact]
        public void McpJsonFileDetectsVisualStudio()
        {
            File.WriteAllText(Path.Combine(UserProfile, ".mcp.json"), "{}");
            Assert.Contains("visual-studio", Detect());
        }

        [Fact]
        public void VsEditionDirDetectsVisualStudio()
        {
            Directory.CreateDirectory(
                Path.Combine(ProgramFiles, "Microsoft Visual Studio", "2022", "Community"));
            Assert.Contains("visual-studio", Detect());
        }

        [Fact]
        public void SeveralPresentReturnsExactSubsetInCanonicalOrder()
        {
            Directory.CreateDirectory(Path.Combine(AppData, "Code"));
            Directory.CreateDirectory(Path.Combine(UserProfile, ".codex"));
            Directory.CreateDirectory(Path.Combine(UserProfile, ".cursor"));
            IReadOnlyList<string> result = Detect();
            Assert.Equal(new[] { "vscode-mcp", "codex", "cursor" }, result);
        }
    }
}
