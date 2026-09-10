import * as cp from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

function publishedServer(extensionRoot: string, runRoot: string): string {
    const explicit = process.env.ROSLYNSENSE_TEST_SERVER;
    if (explicit) return path.resolve(explicit);

    const repositoryRoot = path.resolve(extensionRoot, '..');
    const output = path.join(runRoot, 'server');
    fs.mkdirSync(output, { recursive: true });

    // RoslynMCP's build targets invoke projects that are not regular project
    // references. Restore the solution so those projects also have assets in a
    // clean checkout before publishing the temporary integration-test server.
    const restore = cp.spawnSync(
        'dotnet',
        ['restore', path.join(repositoryRoot, 'RoslynMCP.sln'), '--nologo'],
        { stdio: 'inherit', shell: false }
    );
    if (restore.status !== 0) throw new Error(`Solution restore failed with ${restore.status}.`);

    const result = cp.spawnSync(
        'dotnet',
        [
            'publish',
            path.join(repositoryRoot, 'RoslynMCP', 'RoslynMCP.csproj'),
            '--configuration', 'Release',
            '--output', output,
            '--no-restore',
            '--nologo',
            '-p:Version=0.3.0',
            '-p:BuildDebugWorkers=false',
            '-p:BuildTrayIcon=false',
        ],
        { stdio: 'inherit', shell: false }
    );
    if (result.status !== 0) throw new Error(`Temporary server publish failed with ${result.status}.`);
    const agent = path.join(output, 'hotreload', 'RoslynMCP.HotReloadAgent.dll');
    if (!fs.existsSync(agent)) throw new Error(`Published server is missing its hot reload startup hook: ${agent}.`);
    // `ToolCommandName` names the NuGet shim; a direct publish keeps the assembly/apphost name.
    return path.join(output, process.platform === 'win32' ? 'RoslynMCP.exe' : 'RoslynMCP');
}

async function main(): Promise<void> {
    const extensionRoot = path.resolve(__dirname, '../../..');
    const runs = path.join(extensionRoot, '.vscode-test', 'integration-runs');
    fs.mkdirSync(runs, { recursive: true });
    const runRoot = fs.mkdtempSync(path.join(runs, 'run-'));
    const workspace = path.join(runRoot, 'workspace');
    const userData = path.join(runRoot, 'user-data');
    const temp = path.join(runRoot, 'temp');
    fs.mkdirSync(temp, { recursive: true });
    fs.mkdirSync(path.join(userData, 'User'), { recursive: true });
    fs.writeFileSync(path.join(userData, 'User', 'settings.json'), JSON.stringify({
        'files.autoSave': 'off', 'files.hotExit': 'off', 'roslynSense.hotReload.applyOnSave': false,
    }));
    // Keep source edits, recovered editor buffers and personal server settings out of later
    // runs. Retain this directory for diagnostics when the real editor test fails.
    fs.cpSync(path.join(extensionRoot, 'src/test/integration/fixture'), workspace, {
        recursive: true, filter: source => !['bin', 'obj', '.vscode'].includes(path.basename(source)),
    });
    // A Unix domain socket path cannot exceed 108 bytes, and .NET puts a named pipe under
    // TMPDIR/.dotnet/corefx/pipe/. This run root is nested six directories deep inside the
    // checkout, so pointing TMPDIR straight at it left the hot reload agent's pipe unbindable —
    // the server's listener and the agent's connect both failed silently and every apply came
    // back with nothing running to apply it to. The link keeps the path short while the files
    // themselves still land in the run root, where the CI job collects them.
    const shortTemp = process.platform === 'win32'
        ? temp
        : path.join(os.tmpdir(), `rs-${path.basename(runRoot)}`);
    if (shortTemp !== temp) {
        try { fs.unlinkSync(shortTemp); } catch { /* first run for this name */ }
        fs.symlinkSync(temp, shortTemp, 'dir');
        const budget = shortTemp.length + '/.dotnet/corefx/pipe/'.length + 'roslyn-sense-hotreload-000000'.length;
        if (budget >= 108) throw new Error(`Temp link '${shortTemp}' leaves no room for a pipe path (${budget} bytes).`);
    }
    console.log(`VS Code integration workspace and diagnostics: ${runRoot}`);
    const server = publishedServer(extensionRoot, runRoot);
    if (!fs.existsSync(server)) throw new Error(`Test server was not found at ${server}.`);

    const fixture = cp.spawnSync('dotnet', ['build', path.join(workspace, 'Fixture.csproj'), '--nologo'],
        { stdio: 'inherit', shell: false });
    if (fixture.status !== 0) throw new Error(`Integration fixture build failed with ${fixture.status}.`);

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
            ROSLYNSENSE_HOME: path.join(runRoot, 'roslynsense-home'),
            TEMP: temp, TMP: temp, TMPDIR: shortTemp,
        },
        launchArgs: [
            workspace,
            `--user-data-dir=${userData}`,
            `--extensions-dir=${path.join(runRoot, 'extensions')}`,
            '--skip-welcome',
            '--skip-release-notes',
        ],
    });
}

void main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
