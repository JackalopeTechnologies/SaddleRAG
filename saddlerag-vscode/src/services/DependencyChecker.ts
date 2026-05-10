// DependencyChecker.ts
import { execFile } from 'child_process';
import * as path from 'path';
import type { DetectResult } from '../models/ServiceStatus';
import { WINDOWS_PLATFORM, WINDOWS_SUBFOLDER, POSIX_SUBFOLDER, POWERSHELL_CMD, BASH_CMD, PS1_EXT, SH_EXT, POWERSHELL_SCRIPT_FLAG } from './ScriptPaths';

type Dependency = 'mongodb' | 'ollama';

const NOT_INSTALLED_EXIT_CODE = 2;

export class DependencyChecker
{
    private readonly scriptsRoot: string;

    constructor(scriptsRoot: string)
    {
        this.scriptsRoot = scriptsRoot;
    }

    scriptPath(scriptName: string, platform: NodeJS.Platform = process.platform): string
    {
        const isWindows = platform === WINDOWS_PLATFORM;
        const folder = isWindows ? WINDOWS_SUBFOLDER : POSIX_SUBFOLDER;
        const ext = isWindows ? PS1_EXT : SH_EXT;
        return path.join(this.scriptsRoot, folder, `${scriptName}${ext}`);
    }

    parseDetectOutput(exitCode: number, stdout: string): DetectResult
    {
        let result: DetectResult = { status: 'not-installed' };
        try
        {
            const parsed = JSON.parse(stdout) as { status: string; port?: number; path?: string };
            switch (exitCode)
            {
                case 0:
                    result = { status: 'running', port: parsed['port'] };
                    break;
                case 1:
                    result = { status: 'stopped', path: parsed['path'] };
                    break;
                default:
                    result = { status: 'not-installed' };
                    break;
            }
        }
        catch
        {
            // stdout was not valid JSON; result stays not-installed
        }
        return result;
    }

    async check(dependency: Dependency): Promise<DetectResult>
    {
        const scriptName = `detect-${dependency}`;
        const script = this.scriptPath(scriptName);

        return new Promise((resolve) =>
        {
            const isWindows = process.platform === WINDOWS_PLATFORM;
            const cmd = isWindows ? POWERSHELL_CMD : BASH_CMD;
            const args = isWindows ? [POWERSHELL_SCRIPT_FLAG, script] : [script];

            execFile(cmd, args, (error, stdout) =>
            {
                const code = error?.code;
                const exitCode = error === null ? 0 : typeof code === 'number' ? code : NOT_INSTALLED_EXIT_CODE;
                resolve(this.parseDetectOutput(exitCode, stdout.trim()));
            });
        });
    }
}
