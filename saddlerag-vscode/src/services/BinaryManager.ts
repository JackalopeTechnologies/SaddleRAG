// BinaryManager.ts
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import * as https from 'https';
import { execFile } from 'child_process';

const BINARY_NAME_POSIX = 'SaddleRAG.Mcp';
const BINARY_NAME_WIN = 'SaddleRAG.Mcp.exe';
const GITHUB_RELEASES_HOST = 'api.github.com';
const GITHUB_RELEASES_PATH = '/repos/JackalopeTechnologies/saddlerag/releases/latest';
const USER_AGENT = 'saddlerag-vscode';
const GITHUB_DOWNLOAD_BASE = 'https://github.com/JackalopeTechnologies/saddlerag/releases/download';

export class BinaryManager
{
    private readonly storagePath: string;
    private readonly version: string;

    constructor(globalStoragePath: string, version: string)
    {
        this.storagePath = globalStoragePath;
        this.version = version;
    }

    get platformRid(): string
    {
        const arch = os.arch() === 'arm64' ? 'arm64' : 'x64';
        const rid = process.platform === 'win32'
            ? 'win-x64'
            : process.platform === 'darwin'
                ? (arch === 'arm64' ? 'osx-arm64' : 'osx-x64')
                : (arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
        return rid;
    }

    get artifactName(): string
    {
        const rid = this.platformRid;
        const name = process.platform === 'win32'
            ? `SaddleRAG.Mcp-${this.version}-${rid}.zip`
            : `saddlerag-${this.version}-${rid}.tar.gz`;
        return name;
    }

    get binaryPath(): string
    {
        const name = process.platform === 'win32' ? BINARY_NAME_WIN : BINARY_NAME_POSIX;
        return path.join(this.storagePath, this.version, this.platformRid, name);
    }

    async isBinaryPresent(): Promise<boolean>
    {
        let present = false;
        try
        {
            await fs.access(this.binaryPath);
            present = true;
        }
        catch
        {
            // File does not exist
        }
        return present;
    }

    async fetchLatestVersion(): Promise<string>
    {
        return new Promise((resolve, reject) =>
        {
            const options = {
                hostname: GITHUB_RELEASES_HOST,
                path: GITHUB_RELEASES_PATH,
                headers: { 'User-Agent': USER_AGENT }
            };
            https.get(options, (res) =>
            {
                let data = '';
                res.on('data', (chunk: Buffer) => { data += chunk.toString(); });
                res.on('end', () =>
                {
                    try
                    {
                        const json = JSON.parse(data) as { tag_name: string };
                        resolve(json.tag_name.replace(/^v/, ''));
                    }
                    catch (e)
                    {
                        reject(new Error(`Failed to parse GitHub release response: ${String(e)}`));
                    }
                });
            }).on('error', reject);
        });
    }

    async downloadBinary(progressCallback?: (downloaded: number, total: number) => void): Promise<void>
    {
        const dir = path.dirname(this.binaryPath);
        await fs.mkdir(dir, { recursive: true });

        const downloadUrl = `${GITHUB_DOWNLOAD_BASE}/v${this.version}/${this.artifactName}`;
        const archivePath = path.join(dir, this.artifactName);

        await this.downloadFile(downloadUrl, archivePath, progressCallback);
        await this.extractArchive(archivePath, dir);
        await fs.rm(archivePath, { force: true });

        if (process.platform !== 'win32')
        {
            await fs.chmod(this.binaryPath, 0o755);
        }
    }

    private async downloadFile(url: string, dest: string, progressCallback?: (d: number, t: number) => void): Promise<void>
    {
        return new Promise((resolve, reject) =>
        {
            // eslint-disable-next-line @typescript-eslint/no-var-requires
            const file = (require('fs') as typeof import('fs')).createWriteStream(dest);
            https.get(url, { headers: { 'User-Agent': USER_AGENT } }, (res) =>
            {
                if (res.statusCode !== 200)
                {
                    file.destroy();
                    reject(new Error(`Download failed: HTTP ${res.statusCode ?? 'unknown'} from ${url}`));
                    return;
                }
                const total = parseInt(res.headers['content-length'] ?? '0', 10);
                let downloaded = 0;
                res.on('data', (chunk: Buffer) =>
                {
                    downloaded += chunk.length;
                    progressCallback?.(downloaded, total);
                });
                res.pipe(file);
                file.on('finish', () => { file.close(); resolve(); });
            }).on('error', reject);
        });
    }

    private async extractArchive(archivePath: string, destDir: string): Promise<void>
    {
        return new Promise<void>((resolve, reject) =>
        {
            if (process.platform === 'win32')
            {
                execFile(
                    'powershell',
                    ['-Command', `Expand-Archive -Path "${archivePath}" -DestinationPath "${destDir}" -Force`],
                    (err) => { if (err) { reject(err); } else { resolve(); } }
                );
            }
            else
            {
                execFile(
                    'tar',
                    ['-xzf', archivePath, '-C', destDir],
                    (err) => { if (err) { reject(err); } else { resolve(); } }
                );
            }
        });
    }
}
