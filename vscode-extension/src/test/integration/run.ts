import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

function publishedServer(extensionRoot: string): string {
    const explicit = process.env.ROSLYNSENSE_TEST_SERVER;
    if (explicit) return path.resolve(explicit);

    const repositoryRoot = path.resolve(extensionRoot, '..');
    const output = path.join(extensionRoot, '.vscode-test', 'roslyn-sense-server');
    fs.mkdirSync(output, { recursive: true });
    const result = cp.spawnSync(
        'dotnet',
        [
            'publish',
            path.join(repositoryRoot, 'RoslynMCP', 'RoslynMCP.csproj'),
            '--configuration', 'Release',
            '--output', output,
            '--nologo',
            '-p:Version=0.3.0',
            '-p:BuildDebugWorkers=false',
            '-p:BuildTrayIcon=false',
        ],
        { stdio: 'inherit', shell: false }
    );
    if (result.status !== 0) throw new Error(`Temporary server publish failed with ${result.status}.`);
    // `ToolCommandName` names the NuGet shim; a direct publish keeps the assembly/apphost name.
    return path.join(output, process.platform === 'win32' ? 'RoslynMCP.exe' : 'RoslynMCP');
}

async function main(): Promise<void> {
    const extensionRoot = path.resolve(__dirname, '../../..');
    const server = publishedServer(extensionRoot);
    if (!fs.existsSync(server)) throw new Error(`Test server was not found at ${server}.`);

    const version = cp.spawnSync(server, ['--version'], { encoding: 'utf8', shell: false });
    if (version.status !== 0 || !/^\d+\.\d+\.\d+\s*$/.test(version.stdout)) {
        throw new Error(`Test server version probe failed: ${version.stderr || version.stdout}`);
    }

    await runTests({
        version: process.env.VSCODE_VERSION || 'stable',
        extensionDevelopmentPath: extensionRoot,
        extensionTestsPath: path.join(__dirname, 'suite', 'index'),
        extensionTestsEnv: {
            ROSLYNSENSE_SERVER: server,
            ROSLYNSENSE_LSP_TRACE: '1',
            ROSLYNMCP_SHARED_HOST: '0',
            ROSLYNMCP_NO_UPDATE_CHECK: '1',
        },
        launchArgs: [
            path.join(extensionRoot, 'src', 'test', 'integration', 'fixture'),
            '--skip-welcome',
            '--skip-release-notes',
        ],
    });
}

void main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
