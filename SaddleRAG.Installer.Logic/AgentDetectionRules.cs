// AgentDetectionRules.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaddleRAG.Installer.Logic
{
    /// <summary>
    /// Pure decision rules mirroring the client writers' <c>IsDetected()</c> path checks.
    /// Given root directories (injectable for tests), reports which agents are detected.
    /// </summary>
    public static class AgentDetectionRules
    {
        #region Agent key constants

        private const string ClaudeCodeKey = "claude-code";
        private const string ClaudeDesktopKey = "claude-desktop";
        private const string VsCodeKey = "vscode-mcp";
        private const string CopilotCliKey = "copilot-cli";
        private const string CodexKey = "codex";
        private const string CursorKey = "cursor";
        private const string GeminiCliKey = "gemini-cli";
        private const string WindsurfKey = "windsurf";
        private const string VisualStudioKey = "visual-studio";

        #endregion

        #region Path segment constants

        private const string ClaudeDirName = ".claude";
        private const string ClaudeJsonFileName = ".claude.json";
        private const string ClaudeAppDataDir = "Claude";
        private const string CodeAppDataDir = "Code";
        private const string CopilotDirName = ".copilot";
        private const string CodexDirName = ".codex";
        private const string CursorDirName = ".cursor";
        private const string GeminiDirName = ".gemini";
        private const string CodeiumDirName = ".codeium";
        private const string WindsurfDirName = "windsurf";
        private const string McpJsonFileName = ".mcp.json";
        private const string VsRootDir = "Microsoft Visual Studio";
        private const string VsYear = "2022";
        private const string VsEditionCommunity = "Community";
        private const string VsEditionProfessional = "Professional";
        private const string VsEditionEnterprise = "Enterprise";
        private const string VsEditionPreview = "Preview";

        #endregion

        #region Static fields

        private static readonly string[] smVsEditions =
        {
            VsEditionCommunity,
            VsEditionProfessional,
            VsEditionEnterprise,
            VsEditionPreview
        };

        private static readonly string[] smCanonicalOrder =
        {
            ClaudeCodeKey,
            ClaudeDesktopKey,
            VsCodeKey,
            CopilotCliKey,
            CodexKey,
            CursorKey,
            GeminiCliKey,
            WindsurfKey,
            VisualStudioKey
        };

        #endregion

        /// <summary>
        /// Returns the keys of all agents detected under the supplied root directories,
        /// in canonical order.
        /// </summary>
        /// <param name="userProfile">The user profile root (e.g. <c>%USERPROFILE%</c>).</param>
        /// <param name="appData">The roaming AppData root (e.g. <c>%APPDATA%</c>).</param>
        /// <param name="programFiles">The Program Files root.</param>
        public static IReadOnlyList<string> DetectInstalledAgents(
            string userProfile, string appData, string programFiles)
        {
            if (userProfile is null)
            {
                throw new ArgumentNullException(nameof(userProfile));
            }
            if (appData is null)
            {
                throw new ArgumentNullException(nameof(appData));
            }
            if (programFiles is null)
            {
                throw new ArgumentNullException(nameof(programFiles));
            }

            IReadOnlyList<string> res = smCanonicalOrder
                .Where(key => IsAgentDetected(key, userProfile, appData, programFiles))
                .ToArray();
            return res;
        }

        /// <summary>
        /// Returns whether a single agent key is detected under the supplied root directories.
        /// </summary>
        /// <param name="key">One of the canonical agent keys.</param>
        /// <param name="userProfile">The user profile root.</param>
        /// <param name="appData">The roaming AppData root.</param>
        /// <param name="programFiles">The Program Files root.</param>
        public static bool IsAgentDetected(
            string key, string userProfile, string appData, string programFiles)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (userProfile is null)
            {
                throw new ArgumentNullException(nameof(userProfile));
            }
            if (appData is null)
            {
                throw new ArgumentNullException(nameof(appData));
            }
            if (programFiles is null)
            {
                throw new ArgumentNullException(nameof(programFiles));
            }

            bool res = key switch
            {
                ClaudeCodeKey => DetectClaudeCode(userProfile),
                ClaudeDesktopKey => Directory.Exists(Path.Combine(appData, ClaudeAppDataDir)),
                VsCodeKey => Directory.Exists(Path.Combine(appData, CodeAppDataDir)),
                CopilotCliKey => Directory.Exists(Path.Combine(userProfile, CopilotDirName)),
                CodexKey => Directory.Exists(Path.Combine(userProfile, CodexDirName)),
                CursorKey => Directory.Exists(Path.Combine(userProfile, CursorDirName)),
                GeminiCliKey => Directory.Exists(Path.Combine(userProfile, GeminiDirName)),
                WindsurfKey => Directory.Exists(Path.Combine(userProfile, CodeiumDirName, WindsurfDirName)),
                VisualStudioKey => DetectVisualStudio(userProfile, programFiles),
                _ => false
            };
            return res;
        }

        private static bool DetectClaudeCode(string userProfile)
        {
            string jsonPath = Path.Combine(userProfile, ClaudeJsonFileName);
            string dirPath = Path.Combine(userProfile, ClaudeDirName);
            bool res = File.Exists(jsonPath) || Directory.Exists(dirPath);
            return res;
        }

        private static bool DetectVisualStudio(string userProfile, string programFiles)
        {
            string mcpPath = Path.Combine(userProfile, McpJsonFileName);
            bool res = File.Exists(mcpPath);
            if (!res)
            {
                string vsBase = Path.Combine(programFiles, VsRootDir, VsYear);
                res = smVsEditions.Any(e => Directory.Exists(Path.Combine(vsBase, e)));
            }
            return res;
        }
    }
}
