// McpRegistrar.ts
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';

export const SADDLERAG_SERVER_KEY = 'saddlerag';
const DEFAULT_MCP_URL = 'http://localhost:6100/mcp';
export const SADDLERAG_MCP_ENTRY = { type: 'http', url: DEFAULT_MCP_URL } as const;
const SERVERS_KEY = 'servers';

export class McpRegistrar
{
    private readonly configPath: string;
    private readonly mcpUrl: string;

    constructor(configPath?: string, mcpUrl?: string)
    {
        this.configPath = configPath ?? McpRegistrar.defaultConfigPath();
        this.mcpUrl = mcpUrl ?? DEFAULT_MCP_URL;
    }

    static defaultConfigPath(): string
    {
        let result: string;
        switch (process.platform)
        {
            case 'win32':
                result = path.join(process.env['APPDATA'] ?? os.homedir(), 'Code', 'User', 'mcp.json');
                break;
            case 'darwin':
                result = path.join(os.homedir(), 'Library', 'Application Support', 'Code', 'User', 'mcp.json');
                break;
            default:
                result = path.join(os.homedir(), '.config', 'Code', 'User', 'mcp.json');
                break;
        }
        return result;
    }

    async register(): Promise<void>
    {
        const config = await this.readConfig();
        const servers = this.ensureServers(config);
        servers[SADDLERAG_SERVER_KEY] = { type: 'http', url: this.mcpUrl };
        await this.writeConfig(config);
    }

    async unregister(): Promise<void>
    {
        try
        {
            const config = await this.readConfig();
            const servers = config[SERVERS_KEY] as Record<string, unknown> | undefined;
            if (servers !== undefined)
            {
                delete servers[SADDLERAG_SERVER_KEY];
                await this.writeConfig(config);
            }
        }
        catch
        {
            // File absent — nothing to unregister
        }
    }

    async isRegistered(): Promise<boolean>
    {
        let result = false;
        try
        {
            const config = await this.readConfig();
            const servers = config[SERVERS_KEY] as Record<string, unknown> | undefined;
            result = servers?.[SADDLERAG_SERVER_KEY] !== undefined;
        }
        catch
        {
            result = false;
        }
        return result;
    }

    private ensureServers(config: Record<string, unknown>): Record<string, unknown>
    {
        const existing = config[SERVERS_KEY];
        const servers: Record<string, unknown> =
            existing !== null && typeof existing === 'object' && !Array.isArray(existing)
                ? existing as Record<string, unknown>
                : {};
        config[SERVERS_KEY] = servers;
        return servers;
    }

    private async readConfig(): Promise<Record<string, unknown>>
    {
        let result: Record<string, unknown> = {};
        try
        {
            const raw = await fs.readFile(this.configPath, 'utf-8');
            result = JSON.parse(raw) as Record<string, unknown>;
        }
        catch
        {
            result = {};
        }
        return result;
    }

    private async writeConfig(config: Record<string, unknown>): Promise<void>
    {
        await fs.mkdir(path.dirname(this.configPath), { recursive: true });
        const tmp = `${this.configPath}.tmp`;
        await fs.writeFile(tmp, JSON.stringify(config, null, 2), { encoding: 'utf-8' });
        await fs.rename(tmp, this.configPath);
    }
}
