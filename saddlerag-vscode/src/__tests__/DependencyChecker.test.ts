// DependencyChecker.test.ts
import * as path from 'path';
import { execFile } from 'child_process';
import { DependencyChecker } from '../services/DependencyChecker';

jest.mock('child_process');

describe('DependencyChecker', () => {
    it('parseDetectOutput: exit 0 returns running', () => {
        const checker = new DependencyChecker('/scripts');
        const result = checker.parseDetectOutput(0, '{"status":"running","port":27017}');
        expect(result.status).toBe('running');
        expect(result.port).toBe(27017);
    });

    it('parseDetectOutput: exit 1 returns stopped', () => {
        const checker = new DependencyChecker('/scripts');
        const result = checker.parseDetectOutput(1, '{"status":"stopped","path":"/usr/bin/mongod"}');
        expect(result.status).toBe('stopped');
        expect(result.path).toBe('/usr/bin/mongod');
    });

    it('parseDetectOutput: exit 2 returns not-installed', () => {
        const checker = new DependencyChecker('/scripts');
        const result = checker.parseDetectOutput(2, '{"status":"not-found"}');
        expect(result.status).toBe('not-installed');
    });

    it('parseDetectOutput: non-zero exit with parse failure returns not-installed', () => {
        const checker = new DependencyChecker('/scripts');
        const result = checker.parseDetectOutput(1, 'invalid json');
        expect(result.status).toBe('not-installed');
    });

    it('scriptPath: returns windows path on win32', () => {
        const checker = new DependencyChecker('/scripts');
        const p = checker.scriptPath('detect-mongodb', 'win32');
        expect(p).toContain('windows');
        expect(p.endsWith('.ps1')).toBe(true);
    });

    it('scriptPath: returns posix path on linux', () => {
        const checker = new DependencyChecker('/scripts');
        const p = checker.scriptPath('detect-mongodb', 'linux');
        expect(p).toContain('posix');
        expect(p.endsWith('.sh')).toBe(true);
    });

    describe('check', () => {
        it('returns running when script exits 0', async () => {
            const mockExecFile = execFile as jest.MockedFunction<typeof execFile>;
            mockExecFile.mockImplementation((_cmd, _args, callback: any) => {
                callback(null, '{"status":"running","port":27017}', '');
                return {} as any;
            });

            const checker = new DependencyChecker('/scripts');
            const result = await checker.check('mongodb');
            expect(result.status).toBe('running');
        });

        it('returns not-installed when script binary not found (ENOENT)', async () => {
            const mockExecFile = execFile as jest.MockedFunction<typeof execFile>;
            const err = Object.assign(new Error('ENOENT'), { code: 'ENOENT' });
            mockExecFile.mockImplementation((_cmd, _args, callback: any) => {
                callback(err, '', '');
                return {} as any;
            });

            const checker = new DependencyChecker('/scripts');
            const result = await checker.check('mongodb');
            expect(result.status).toBe('not-installed');
        });
    });
});
