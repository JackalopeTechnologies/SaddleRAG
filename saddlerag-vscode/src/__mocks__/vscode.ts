// src/__mocks__/vscode.ts
export const window = {
    createTerminal: jest.fn(),
    showErrorMessage: jest.fn(),
    showInformationMessage: jest.fn(),
    createOutputChannel: jest.fn(() => ({ appendLine: jest.fn(), show: jest.fn() })),
    registerTreeDataProvider: jest.fn(),
    withProgress: jest.fn()
};
export const commands = { registerCommand: jest.fn(), executeCommand: jest.fn() };
export const env = { openExternal: jest.fn() };
export const Uri = { parse: jest.fn((s: string) => ({ toString: () => s })), file: jest.fn((s: string) => ({ fsPath: s })) };
export const TreeItem = class {
    label: string;
    collapsibleState: number;
    description?: string;
    tooltip?: string;
    iconPath?: unknown;
    contextValue?: string;
    constructor(label: string, collapsibleState?: number) {
        this.label = label;
        this.collapsibleState = collapsibleState ?? 0;
    }
};
export const TreeItemCollapsibleState = { None: 0, Collapsed: 1, Expanded: 2 };
export const EventEmitter = class {
    event: unknown = jest.fn();
    fire = jest.fn();
};
export const ThemeIcon = class {
    constructor(public id: string, public color?: unknown) {}
};
export const ThemeColor = class {
    constructor(public id: string) {}
};
export const ExtensionContext = {};
export const ProgressLocation = { Notification: 15 };
