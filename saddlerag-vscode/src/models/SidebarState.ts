// SidebarState.ts

import type { ServiceStatus } from './ServiceStatus';

export interface LibraryStatus {
    name: string;
    version: string;
    health: string;
}

export interface ActiveJob {
    id: string;
    library: string;
    phase: string;
}

export interface SidebarState {
    mongodb: ServiceStatus;
    ollama: ServiceStatus;
    saddlerag: ServiceStatus;
    libraries: LibraryStatus[];
    activeJobs: ActiveJob[];
}

export const emptySidebarState: SidebarState = {
    mongodb: 'unknown',
    ollama: 'unknown',
    saddlerag: 'unknown',
    libraries: [],
    activeJobs: []
};
