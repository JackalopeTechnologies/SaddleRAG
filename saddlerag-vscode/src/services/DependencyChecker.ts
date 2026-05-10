// DependencyChecker.ts
import { execFile } from 'child_process';
import * as path from 'path';
import type { DetectResult } from '../models/ServiceStatus';

type Dependency = 'mongodb' | 'ollama';

export class DependencyChecker
{
    private readonly scriptsRoot: string;

    constructor(scriptsRoot: string)
    {
        this.scriptsRoot = scriptsRoot;
    }

    scriptPath(scriptName: string, platform: NodeJS.Platform = process.platform): string
    {
        const isWindows = platform === 'win32';
        const folder = isWindows ? 'windows' : 'posix';
        const ext = isWindows ? '.ps1' : '.sh';
        return path.join(this.scriptsRoot, folder, `${scriptName}${ext}`);
    }

    parseDetectOutput(exitCode: number, stdout: string): DetectResult
    {
        try
        {
            const parsed = JSON.parse(stdout) as { status: string; port?: number; path?: string };
            const result: DetectResult = exitCode === 0
                ? { status: 'running', port: parsed['port'] }
                : exitCode === 1
                    ? { status: 'stopped', path: parsed['path'] }
                    : { status: 'not-installed' };
            return result;
        }
        catch
        {
            return { status: 'not-installed' };
        }
    }

    async check(dependency: Dependency): Promise<DetectResult>
    {
        const scriptName = `detect-${dependency}`;
        const script = this.scriptPath(scriptName);

        return new Promise((resolve) =>
        {
            const cmd = process.platform === 'win32' ? 'powershell' : 'bash';
            const args = process.platform === 'win32' ? ['-File', script] : [script];

            execFile(cmd, args, (error, stdout) =>
            {
                const code = error?.code;
                const exitCode = typeof code === 'number' ? code : 0;
                resolve(this.parseDetectOutput(exitCode, stdout.trim()));
            });
        });
    }
}
