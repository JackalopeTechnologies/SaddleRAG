// ServiceStatus.ts

export type ServiceStatus = 'running' | 'stopped' | 'not-installed' | 'unknown';

export interface DetectResult {
    status: ServiceStatus;
    port?: number;
    path?: string;
}
