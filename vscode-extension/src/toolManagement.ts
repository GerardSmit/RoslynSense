import * as cp from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

export interface ProcessResult {
    readonly exitCode: number;
    readonly stdout: string;
    readonly stderr: string;
}

export type ProcessOutput = (text: string) => void;
export type ProcessRunner = (
    command: string,
    args: readonly string[],
    output?: ProcessOutput,
    signal?: AbortSignal
) => Promise<ProcessResult>;

/** The platform-specific shim created by `dotnet tool install --global`. */
export function globalToolPath(cliHome: string, platform: NodeJS.Platform = process.platform): string {
    const executable = platform === 'win32' ? 'roslyn-sense.exe' : 'roslyn-sense';
    const pathApi = platform === 'win32' ? path.win32 : path.posix;
    return pathApi.join(cliHome, '.dotnet', 'tools', executable);
}

function toolPathExecutable(directory: string, platform: NodeJS.Platform = process.platform): string {
    return path.join(directory, platform === 'win32' ? 'roslyn-sense.exe' : 'roslyn-sense');
}

/** A failed child process with enough context for the UI and CI logs to be actionable. */
export class ToolCommandError extends Error {
    constructor(
        readonly command: string,
        readonly args: readonly string[],
        readonly result: ProcessResult
    ) {
        const detail = result.stderr.trim() || result.stdout.trim() || 'No output was produced.';
        super(`'${command} ${args.join(' ')}' exited with code ${result.exitCode}. ${detail}`);
        this.name = 'ToolCommandError';
    }
}

/** Run without a shell, independent of the user's configured terminal and its quoting rules. */
export const runProcess: ProcessRunner = (command, args, output, signal) =>
    new Promise<ProcessResult>((resolve, reject) => {
        const child = cp.spawn(command, [...args], {
            shell: false,
            windowsHide: true,
            env: process.env,
        });
        let stdout = '';
        let stderr = '';

        const collect = (kind: 'stdout' | 'stderr', chunk: Buffer): void => {
            const text = chunk.toString('utf8');
            if (kind === 'stdout') stdout += text;
            else stderr += text;
            output?.(text);
        };
        child.stdout.on('data', (chunk: Buffer) => collect('stdout', chunk));
        child.stderr.on('data', (chunk: Buffer) => collect('stderr', chunk));
        child.once('error', reject);
        child.once('close', (code) => resolve({ exitCode: code ?? -1, stdout, stderr }));

        if (signal) {
            const cancel = (): void => { child.kill(); };
            if (signal.aborted) cancel();
            else {
                signal.addEventListener('abort', cancel, { once: true });
                child.once('close', () => signal.removeEventListener('abort', cancel));
            }
        }
    });

async function checked(
    runner: ProcessRunner,
    command: string,
    args: readonly string[],
    output?: ProcessOutput,
    signal?: AbortSignal
): Promise<ProcessResult> {
    output?.(`> ${command} ${args.join(' ')}\n`);
    const result = await runner(command, args, output, signal);
    if (result.exitCode !== 0) throw new ToolCommandError(command, args, result);
    return result;
}

export function installGlobalTool(
    output?: ProcessOutput,
    signal?: AbortSignal,
    runner: ProcessRunner = runProcess
): Promise<ProcessResult> {
    return checked(runner, 'dotnet', ['tool', 'install', '--global', 'RoslynSense'], output, signal);
}

/** Stop every process locking the installed package before asking dotnet to replace it. */
export async function updateGlobalTool(
    output?: ProcessOutput,
    signal?: AbortSignal,
    runner: ProcessRunner = runProcess
): Promise<ProcessResult> {
    // The installed version may predate --stop-daemons. Install the newest package beside it and
    // use that copy as the stopper, avoiding an in-place update while the old store is locked.
    const bootstrapDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'roslyn-sense-update-'));
    try {
        await checked(
            runner,
            'dotnet',
            ['tool', 'install', '--tool-path', bootstrapDirectory, 'RoslynSense'],
            output,
            signal
        );
        await checked(
            runner,
            toolPathExecutable(bootstrapDirectory),
            ['--stop-daemons'],
            output,
            signal
        );
        return await checked(
            runner,
            'dotnet',
            ['tool', 'update', '--global', 'RoslynSense'],
            output,
            signal
        );
    } finally {
        try {
            fs.rmSync(bootstrapDirectory, { recursive: true, force: true });
        } catch {
            // A failed cleanup must not turn a successful tool update into an apparent failure.
        }
    }
}
