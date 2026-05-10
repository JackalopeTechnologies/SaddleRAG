// extension.ts
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

import * as vscode from 'vscode';
import * as path from 'path';
import { BinaryManager } from './services/BinaryManager';
import { ProcessManager } from './services/ProcessManager';
import { StatusPoller } from './services/StatusPoller';
import { McpRegistrar } from './services/McpRegistrar';
import { DependencyChecker } from './services/DependencyChecker';
import { ScriptRunner } from './services/ScriptRunner';
import { SaddleRagProvider } from './sidebar/SaddleRagProvider';
import type { SidebarState } from './models/SidebarState';

const SADDLERAG_BASE_URL = 'http://localhost:6100';
const SADDLERAG_VIEW_ID = 'saddleragView';
const BYTES_PER_MEGABYTE = 1024 * 1024;
const RELOAD_BUTTON_LABEL = 'Reload';
const MCP_REGISTERED_MESSAGE = 'SaddleRAG registered as an MCP server. Reload VS Code to activate.';
const RELOAD_WINDOW_COMMAND = 'workbench.action.reloadWindow';

export async function activate(context: vscode.ExtensionContext): Promise<void>
{
    const extensionPath = context.extensionPath;
    const scriptsRoot = path.join(extensionPath, 'scripts');
    const version = context.extension.packageJSON.version as string;

    const binaryManager = new BinaryManager(context.globalStorageUri.fsPath, version);
    const processManager = new ProcessManager();
    const statusPoller = new StatusPoller(SADDLERAG_BASE_URL);
    const mcpRegistrar = new McpRegistrar();
    const checker = new DependencyChecker(scriptsRoot);
    const runner = new ScriptRunner(scriptsRoot);
    const provider = new SaddleRagProvider();

    vscode.window.registerTreeDataProvider(SADDLERAG_VIEW_ID, provider);

    context.subscriptions.push(
        vscode.commands.registerCommand('saddlerag.refresh', () => void statusPoller.pollNow()),
        vscode.commands.registerCommand('saddlerag.openWebHub', () =>
        {
            void vscode.env.openExternal(vscode.Uri.parse(SADDLERAG_BASE_URL));
        }),
        vscode.commands.registerCommand('saddlerag.installMongoDB', () => runner.runInstall('mongodb')),
        vscode.commands.registerCommand('saddlerag.installOllama', () => runner.runInstall('ollama')),
        vscode.commands.registerCommand('saddlerag.downloadSaddleRag', () =>
        {
            void downloadAndStart(binaryManager, processManager, mcpRegistrar);
        })
    );

    statusPoller.onStateChange((state: SidebarState) => provider.updateState(state));

    await checkDependenciesAndStart(checker, processManager, binaryManager, mcpRegistrar, statusPoller);

    statusPoller.start();
}

export function deactivate(): void
{
    // Timers are cleared via context.subscriptions disposal — no explicit action needed
}

async function checkDependenciesAndStart(
    checker: DependencyChecker,
    processManager: ProcessManager,
    binaryManager: BinaryManager,
    mcpRegistrar: McpRegistrar,
    statusPoller: StatusPoller
): Promise<void>
{
    await Promise.all([checker.check('mongodb'), checker.check('ollama')]);

    void statusPoller.pollNow();

    const binaryPresent = await binaryManager.isBinaryPresent();
    if (binaryPresent && !processManager.isRunning)
    {
        await processManager.start(binaryManager.binaryPath);
        await registerMcpIfNeeded(mcpRegistrar);
    }
}

async function registerMcpIfNeeded(mcpRegistrar: McpRegistrar): Promise<void>
{
    const registered = await mcpRegistrar.isRegistered();
    if (!registered)
    {
        await mcpRegistrar.register();
        void vscode.window.showInformationMessage(
            MCP_REGISTERED_MESSAGE,
            RELOAD_BUTTON_LABEL
        ).then(choice =>
        {
            if (choice === RELOAD_BUTTON_LABEL)
            {
                void vscode.commands.executeCommand(RELOAD_WINDOW_COMMAND);
            }
        });
    }
}

async function downloadAndStart(
    binaryManager: BinaryManager,
    processManager: ProcessManager,
    mcpRegistrar: McpRegistrar
): Promise<void>
{
    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Downloading SaddleRAG...', cancellable: false },
        async (progress) =>
        {
            await binaryManager.downloadBinary((downloaded, total) =>
            {
                if (total > 0)
                {
                    const increment = (downloaded / total) * 100;
                    const message = `${Math.round(downloaded / BYTES_PER_MEGABYTE)}MB`;
                    progress.report({ increment, message });
                }
            });
        }
    );

    await processManager.start(binaryManager.binaryPath);

    await registerMcpIfNeeded(mcpRegistrar);
}
