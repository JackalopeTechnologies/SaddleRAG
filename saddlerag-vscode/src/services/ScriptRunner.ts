// ScriptRunner.ts
import * as vscode from 'vscode';
import * as path from 'path';

const TERMINAL_NAME = 'SaddleRAG Setup';

export class ScriptRunner
{
    private readonly scriptsRoot: string;

    constructor(scriptsRoot: string)
    {
        this.scriptsRoot = scriptsRoot;
    }

    runInstall(dependency: 'mongodb' | 'ollama'): void
    {
        const scriptName = `install-${dependency}`;
        const isWindows = process.platform === 'win32';
        const folder = isWindows ? 'windows' : 'posix';
        const ext = isWindows ? '.ps1' : '.sh';
        const scriptPath = path.join(this.scriptsRoot, folder, `${scriptName}${ext}`);

        const terminal = vscode.window.createTerminal(TERMINAL_NAME);
        terminal.show();

        const cmd = isWindows
            ? `powershell -ExecutionPolicy Bypass -File "${scriptPath}"`
            : `bash "${scriptPath}"`;
        terminal.sendText(cmd);
    }
}
