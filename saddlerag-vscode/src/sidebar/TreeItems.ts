// TreeItems.ts
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.
import * as vscode from 'vscode';
import type { ServiceStatus } from '../models/ServiceStatus';

export class SectionItem extends vscode.TreeItem
{
    constructor(label: string, count?: number)
    {
        const displayLabel = count !== undefined ? `${label} (${count})` : label;
        super(displayLabel, vscode.TreeItemCollapsibleState.Expanded);
        this.contextValue = 'section';
    }
}

export class ServiceItem extends vscode.TreeItem
{
    constructor(name: string, status: ServiceStatus)
    {
        super(name, vscode.TreeItemCollapsibleState.None);
        this.iconPath = ServiceItem.IconForStatus(status);
        this.description = status;
        this.contextValue = `service-${status}`;
    }

    private static IconForStatus(status: ServiceStatus): vscode.ThemeIcon
    {
        const STATUS_ICONS: Record<ServiceStatus, vscode.ThemeIcon> =
        {
            'running':       new vscode.ThemeIcon('pass',          new vscode.ThemeColor('testing.iconPassed')),
            'stopped':       new vscode.ThemeIcon('warning',       new vscode.ThemeColor('list.warningForeground')),
            'not-installed': new vscode.ThemeIcon('error',         new vscode.ThemeColor('testing.iconFailed')),
            'unknown':       new vscode.ThemeIcon('circle-outline'),
        };
        return STATUS_ICONS[status];
    }
}

export class LibraryItem extends vscode.TreeItem
{
    constructor(name: string, version: string, health: string)
    {
        super(name, vscode.TreeItemCollapsibleState.None);
        this.description = version;
        this.tooltip = `${name} ${version} — ${health}`;
        this.iconPath = health === 'Healthy'
            ? new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'))
            : new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        this.contextValue = 'library';
    }
}

export class JobItem extends vscode.TreeItem
{
    constructor(id: string, library: string, phase: string)
    {
        super(library, vscode.TreeItemCollapsibleState.None);
        this.description = phase;
        this.tooltip = `Job ${id}: ${library} — ${phase}`;
        this.iconPath = new vscode.ThemeIcon('sync~spin');
        this.contextValue = 'job';
    }
}
