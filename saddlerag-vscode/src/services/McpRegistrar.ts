// McpRegistrar.ts
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';

const SADDLERAG_KEY = 'saddlerag';
const SERVERS_KEY = 'servers';
const MCP_PORT = 6100;
const MCP_ENTRY = {
    type: 'http',
    url: `http://localhost:${MCP_PORT}/mcp`
} as const;

export class McpRegistrar
{
    private readonly configPath: string;

    constructor(configPath?: string)
    {
        this.configPath = configPath ?? McpRegistrar.defaultConfigPath();
    }

    static defaultConfigPath(): string
    {
        const platform = process.platform;
        return platform === 'win32'
            ? path.join(process.env['APPDATA'] ?? os.homedir(), 'Code', 'User', 'mcp.json')
            : platform === 'darwin'
                ? path.join(os.homedir(), 'Library', 'Application Support', 'Code', 'User', 'mcp.json')
                : path.join(os.homedir(), '.config', 'Code', 'User', 'mcp.json');
    }

    async register(): Promise<void>
    {
        const config = await this.readConfig();
        const servers = this.ensureServers(config);
        servers[SADDLERAG_KEY] = MCP_ENTRY;
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
                delete servers[SADDLERAG_KEY];
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
            result = servers?.[SADDLERAG_KEY] !== undefined;
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
