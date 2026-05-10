// ProcessManager.ts
import { spawn, ChildProcess } from 'child_process';

export class ProcessManager {
    private process: ChildProcess | null = null;

    async start(binaryPath: string): Promise<void> {
        let result: void;
        if (this.process === null) {
            this.process = spawn(binaryPath, [], {
                detached: false,
                stdio: 'ignore'
            });
            this.process.on('exit', () => { this.process = null; });
        }
        result = void 0;
        return result;
    }

    async stop(): Promise<void> {
        let result: void;
        if (this.process !== null) {
            this.process.kill('SIGTERM');
            this.process = null;
        }
        result = void 0;
        return result;
    }

    get isRunning(): boolean {
        return this.process !== null;
    }
}
