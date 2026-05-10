import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs/promises';
import { BinaryManager } from '../services/BinaryManager';

const TEST_STORAGE = path.join(os.tmpdir(), `saddlerag-test-${Date.now()}`);

afterAll(async () => {
    await fs.rm(TEST_STORAGE, { recursive: true, force: true });
});

describe('BinaryManager', () => {
    describe('platformRid', () => {
        it('returns a non-empty string', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.0.0');
            expect(manager.platformRid).toBeTruthy();
        });

        it('includes the OS name', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.0.0');
            const rid = manager.platformRid;
            const platform = process.platform;
            if (platform === 'win32') { expect(rid).toContain('win'); }
            if (platform === 'darwin') { expect(rid).toContain('osx'); }
            if (platform === 'linux') { expect(rid).toContain('linux'); }
        });
    });

    describe('artifactName', () => {
        it('returns zip for windows', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.2.3');
            if (process.platform === 'win32') {
                expect(manager.artifactName).toBe('SaddleRAG.Mcp-1.2.3-win-x64.zip');
            }
        });

        it('returns tar.gz for posix', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.2.3');
            if (process.platform !== 'win32') {
                expect(manager.artifactName).toMatch(/^saddlerag-1\.2\.3-.+\.tar\.gz$/);
            }
        });
    });

    describe('binaryPath', () => {
        it('returns path inside globalStoragePath', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.2.3');
            expect(manager.binaryPath).toContain(TEST_STORAGE);
        });

        it('ends with .exe on Windows', () => {
            const manager = new BinaryManager(TEST_STORAGE, '1.2.3');
            if (process.platform === 'win32') {
                expect(manager.binaryPath.endsWith('.exe')).toBe(true);
            }
        });
    });

    describe('isBinaryPresent', () => {
        it('returns false when binary does not exist', async () => {
            const manager = new BinaryManager(path.join(TEST_STORAGE, 'missing'), '1.2.3');
            expect(await manager.isBinaryPresent()).toBe(false);
        });

        it('returns true when binary exists', async () => {
            const storageDir = path.join(TEST_STORAGE, 'present');
            const manager = new BinaryManager(storageDir, '1.2.3');
            await fs.mkdir(path.dirname(manager.binaryPath), { recursive: true });
            await fs.writeFile(manager.binaryPath, 'fake');
            expect(await manager.isBinaryPresent()).toBe(true);
        });
    });
});
