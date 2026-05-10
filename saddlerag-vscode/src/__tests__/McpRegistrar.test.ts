// McpRegistrar.test.ts
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs/promises';
import { McpRegistrar } from '../services/McpRegistrar';

const TEST_DIR = path.join(os.tmpdir(), `saddlerag-mcp-test-${Date.now()}`);
const MCP_FILE = path.join(TEST_DIR, 'mcp.json');

beforeEach(async () => {
    await fs.mkdir(TEST_DIR, { recursive: true });
    await fs.rm(MCP_FILE, { force: true });
});

afterAll(async () => {
    await fs.rm(TEST_DIR, { recursive: true, force: true });
});

describe('McpRegistrar', () => {
    it('creates mcp.json with saddlerag entry when file is missing', async () => {
        const reg = new McpRegistrar(MCP_FILE);
        await reg.register();

        const content = JSON.parse(await fs.readFile(MCP_FILE, 'utf-8')) as Record<string, unknown>;
        const servers = content['servers'] as Record<string, unknown>;
        expect(servers['saddlerag']).toEqual({
            type: 'http',
            url: 'http://localhost:6100/mcp'
        });
    });

    it('preserves existing servers when registering', async () => {
        const existing = { servers: { 'other-server': { type: 'http', url: 'http://other' } } };
        await fs.writeFile(MCP_FILE, JSON.stringify(existing), 'utf-8');

        const reg = new McpRegistrar(MCP_FILE);
        await reg.register();

        const content = JSON.parse(await fs.readFile(MCP_FILE, 'utf-8')) as Record<string, unknown>;
        const servers = content['servers'] as Record<string, unknown>;
        expect(servers['other-server']).toBeDefined();
        expect(servers['saddlerag']).toBeDefined();
    });

    it('unregister removes only saddlerag key', async () => {
        const existing = {
            servers: {
                'other-server': { type: 'http', url: 'http://other' },
                'saddlerag': { type: 'http', url: 'http://localhost:6100/mcp' }
            }
        };
        await fs.writeFile(MCP_FILE, JSON.stringify(existing), 'utf-8');

        const reg = new McpRegistrar(MCP_FILE);
        await reg.unregister();

        const content = JSON.parse(await fs.readFile(MCP_FILE, 'utf-8')) as Record<string, unknown>;
        const servers = content['servers'] as Record<string, unknown>;
        expect(servers['saddlerag']).toBeUndefined();
        expect(servers['other-server']).toBeDefined();
    });

    it('isRegistered returns true when saddlerag entry exists', async () => {
        const existing = { servers: { saddlerag: { type: 'http', url: 'http://localhost:6100/mcp' } } };
        await fs.writeFile(MCP_FILE, JSON.stringify(existing), 'utf-8');

        const reg = new McpRegistrar(MCP_FILE);
        expect(await reg.isRegistered()).toBe(true);
    });

    it('isRegistered returns false when file missing', async () => {
        const reg = new McpRegistrar(MCP_FILE);
        expect(await reg.isRegistered()).toBe(false);
    });
});
