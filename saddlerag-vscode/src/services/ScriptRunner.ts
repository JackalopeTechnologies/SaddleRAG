// ScriptRunner.ts
import * as vscode from 'vscode';
import * as path from 'path';

const TERMINAL_NAME = 'SaddleRAG Setup';
const WINDOWS_PLATFORM = 'win32';
const WINDOWS_SUBFOLDER = 'windows';
const POSIX_SUBFOLDER = 'posix';
const POWERSHELL_CMD = 'powershell';
const BASH_CMD = 'bash';
const PS1_EXT = '.ps1';
const SH_EXT = '.sh';
const EXECUTION_POLICY_FLAG = '-ExecutionPolicy';
const EXECUTION_POLICY_VALUE = 'Bypass';
const POWERSHELL_SCRIPT_FLAG = '-File';

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
        const isWindows = process.platform === WINDOWS_PLATFORM;
        const folder = isWindows ? WINDOWS_SUBFOLDER : POSIX_SUBFOLDER;
        const ext = isWindows ? PS1_EXT : SH_EXT;
        const scriptPath = path.join(this.scriptsRoot, folder, `${scriptName}${ext}`);

        const terminal = vscode.window.createTerminal(TERMINAL_NAME);
        terminal.show();

        const cmd = isWindows
            ? `${POWERSHELL_CMD} ${EXECUTION_POLICY_FLAG} ${EXECUTION_POLICY_VALUE} ${POWERSHELL_SCRIPT_FLAG} "${scriptPath}"`
            : `${BASH_CMD} "${scriptPath}"`;
        terminal.sendText(cmd);
    }
}
