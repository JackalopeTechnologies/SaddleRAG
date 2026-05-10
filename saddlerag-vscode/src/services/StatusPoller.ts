// StatusPoller.ts
import type { SidebarState, LibraryStatus, ActiveJob } from '../models/SidebarState';
import type { ServiceStatus } from '../models/ServiceStatus';
import { emptySidebarState } from '../models/SidebarState';

type StateChangeHandler = (state: SidebarState) => void;

const HEALTH_INTERVAL_MS = 10_000;
const STATUS_INTERVAL_MS = 60_000;

export class StatusPoller
{
    private readonly baseUrl: string;
    private state: SidebarState = { ...emptySidebarState };
    private handlers: StateChangeHandler[] = [];
    private healthTimer: ReturnType<typeof setInterval> | null = null;
    private statusTimer: ReturnType<typeof setInterval> | null = null;

    constructor(baseUrl: string)
    {
        this.baseUrl = baseUrl;
    }

    onStateChange(handler: StateChangeHandler): void
    {
        this.handlers.push(handler);
    }

    start(): void
    {
        void this.pollNow();
        this.healthTimer = setInterval(() => void this.pollHealth(), HEALTH_INTERVAL_MS);
        this.statusTimer = setInterval(() => void this.pollStatus(), STATUS_INTERVAL_MS);
    }

    stop(): void
    {
        if (this.healthTimer !== null)
            clearInterval(this.healthTimer);
        if (this.statusTimer !== null)
            clearInterval(this.statusTimer);
        this.healthTimer = null;
        this.statusTimer = null;
    }

    setDependencyStatuses(mongodb: ServiceStatus, ollama: ServiceStatus): void
    {
        this.updateState({ mongodb, ollama });
    }

    async pollNow(): Promise<void>
    {
        await Promise.all([this.pollHealth(), this.pollStatus()]);
    }

    private async pollHealth(): Promise<void>
    {
        let saddlerag: ServiceStatus;
        try
        {
            const res = await fetch(`${this.baseUrl}/health`);
            saddlerag = res.ok ? 'running' : 'stopped';
        }
        catch
        {
            saddlerag = 'stopped';
        }
        this.updateState({ saddlerag });
    }

    private async pollStatus(): Promise<void>
    {
        try
        {
            const res = await fetch(`${this.baseUrl}/api/status`);
            if (res.ok)
            {
                const data = await res.json() as { libraries: LibraryStatus[]; activeJobs: ActiveJob[] };
                this.updateState({ libraries: data.libraries, activeJobs: data.activeJobs });
            }
        }
        catch
        {
            // SaddleRAG not reachable — leave libraries/jobs stale
        }
    }

    private updateState(partial: Partial<SidebarState>): void
    {
        this.state = { ...this.state, ...partial };
        for (const handler of this.handlers)
        {
            handler(this.state);
        }
    }
}
