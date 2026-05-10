// ProcessManager.ts
import { spawn, ChildProcess } from 'child_process';

const TERMINATION_SIGNAL = 'SIGTERM';

export class ProcessManager {
    private process: ChildProcess | null = null;

    async start(binaryPath: string): Promise<void> {
        if (this.process === null) {
            this.process = spawn(binaryPath, [], {
                detached: false,
                stdio: 'ignore'
            });
            this.process.on('exit', () => { this.process = null; });
        }
    }

    async stop(): Promise<void> {
        if (this.process !== null) {
            this.process.kill(TERMINATION_SIGNAL);
        }
    }

    get isRunning(): boolean {
        return this.process !== null;
    }
}
