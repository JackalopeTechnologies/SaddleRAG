// SaddleRagProvider.ts
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

import * as vscode from 'vscode';
import type { SidebarState } from '../models/SidebarState';
import { emptySidebarState } from '../models/SidebarState';
import { SectionItem, ServiceItem, LibraryItem, JobItem } from './TreeItems';

type AnyTreeItem = SectionItem | ServiceItem | LibraryItem | JobItem;

const SERVICES_LABEL = 'Services';
const LIBRARIES_LABEL = 'Libraries';
const ACTIVE_JOBS_LABEL = 'Active Jobs';

export class SaddleRagProvider implements vscode.TreeDataProvider<AnyTreeItem>
{
    private state: SidebarState = { ...emptySidebarState };
    private readonly emitter = new vscode.EventEmitter<AnyTreeItem | undefined | null>();
    readonly onDidChangeTreeData = this.emitter.event;

    updateState(state: SidebarState): void
    {
        this.state = state;
        this.emitter.fire(undefined);
    }

    getTreeItem(element: AnyTreeItem): vscode.TreeItem
    {
        return element;
    }

    getChildren(element?: AnyTreeItem): AnyTreeItem[]
    {
        let result: AnyTreeItem[];
        if (element === undefined)
        {
            result = this.buildRootItems();
        }
        else if (element instanceof SectionItem)
        {
            result = this.buildSectionChildren(element.label as string);
        }
        else
        {
            result = [];
        }
        return result;
    }

    private buildRootItems(): AnyTreeItem[]
    {
        const items: AnyTreeItem[] = [
            new SectionItem(SERVICES_LABEL),
            new SectionItem(LIBRARIES_LABEL, this.state.libraries.length)
        ];
        if (this.state.activeJobs.length > 0)
        {
            items.push(new SectionItem(ACTIVE_JOBS_LABEL, this.state.activeJobs.length));
        }
        return items;
    }

    private buildSectionChildren(sectionLabel: string): AnyTreeItem[]
    {
        const section = sectionLabel.startsWith(SERVICES_LABEL) ? 'services'
            : sectionLabel.startsWith(LIBRARIES_LABEL) ? 'libraries'
            : sectionLabel.startsWith(ACTIVE_JOBS_LABEL) ? 'jobs'
            : 'unknown';

        let result: AnyTreeItem[];
        switch (section)
        {
            case 'services':
                result = [
                    new ServiceItem('MongoDB', this.state.mongodb),
                    new ServiceItem('Ollama', this.state.ollama),
                    new ServiceItem('SaddleRAG', this.state.saddlerag)
                ];
                break;
            case 'libraries':
                result = this.state.libraries.map(lib => new LibraryItem(lib.name, lib.version, lib.health));
                break;
            case 'jobs':
                result = this.state.activeJobs.map(job => new JobItem(job.id, job.library, job.phase));
                break;
            default:
                result = [];
                break;
        }
        return result;
    }
}
