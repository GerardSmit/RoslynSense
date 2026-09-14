import * as vscode from 'vscode';
import { ChildProcess, spawn } from 'node:child_process';

interface Request extends vscode.DebugProtocolMessage {
    seq: number;
    command: string;
    arguments?: Record<string, any>;
}

let nextRunTask = 0;

/** Runs Ctrl+F5 without loading a debugger, while retaining VS Code's stop/restart lifecycle. */
export class RunAdapter implements vscode.DebugAdapter {
    private readonly messages = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.messages.event;
    private child?: ChildProcess;
    private sequence = 0;
    private ended = false;
    private stopping?: Promise<void>;
    private taskRunId?: string;
    private taskExecution?: vscode.TaskExecution;
    private taskCreated?: Promise<vscode.TaskExecution>;
    private taskDone?: Promise<void>;
    private taskFinished?: () => void;
    private taskLaunch?: Request;
    private taskStopRequested = false;
    private readonly taskListeners: vscode.Disposable[] = [];

    handleMessage(message: vscode.DebugProtocolMessage): void {
        const request = message as Request;
        switch (request.command) {
            case 'initialize':
                // No restart request: VS Code must stop this process and create a new adapter,
                // which runs the extension's build step before launching the replacement.
                this.reply(request, { supportsConfigurationDoneRequest: true, supportsTerminateRequest: true });
                this.event('initialized');
                break;
            case 'launch':
                this.launch(request);
                break;
            case 'configurationDone':
            case 'setExceptionBreakpoints':
                this.reply(request);
                break;
            case 'setBreakpoints':
                this.reply(request, { breakpoints: [] });
                break;
            case 'threads':
                this.reply(request, { threads: [] });
                break;
            case 'disconnect':
            case 'terminate':
                void this.stop().then(() => this.reply(request), error => this.reply(request, {}, String(error)));
                break;
            default:
                this.reply(request, {}, `Unsupported request during Run Without Debugging: ${request.command}`);
        }
    }

    private launch(request: Request): void {
        if (this.child || this.taskRunId || this.ended) { this.reply(request, {}, 'This run has already started.'); return; }
        const config = request.arguments ?? {};
        if (typeof config.program !== 'string') { this.reply(request, {}, 'A program is required.'); return; }
        const env = { ...process.env };
        for (const [key, value] of Object.entries(config.env ?? {})) {
            if (value === null) { delete env[key]; }
            else { env[key] = String(value); }
        }
        const managed = config.program.toLowerCase().endsWith('.dll');
        const command = config.runtimeExecutable ?? (managed ? 'dotnet' : config.program);
        const args = [...(config.runtimeArgs ?? []),
            ...(managed || config.runtimeExecutable ? [config.program] : []), ...(config.args ?? [])];
        if (config.console === 'integratedTerminal') {
            this.launchTask(request, command, args, env);
            return;
        }
        try {
            const child = this.child = spawn(command, args, {
                cwd: config.cwd ?? undefined, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
            });
            let launched = false;
            child.once('spawn', () => {
                launched = true;
                this.event('process', { name: config.program, systemProcessId: child.pid, isLocalProcess: true, startMethod: 'launch' });
                this.reply(request);
            });
            child.stdout?.on('data', data => this.event('output', { category: 'stdout', output: data.toString() }));
            child.stderr?.on('data', data => this.event('output', { category: 'stderr', output: data.toString() }));
            child.once('error', error => {
                if (!launched) { this.reply(request, {}, error.message); }
                else { this.event('output', { category: 'stderr', output: `${error.message}\n` }); }
                this.finish(1);
            });
            child.once('close', code => this.finish(code ?? 0));
        } catch (error) {
            this.reply(request, {}, String(error));
            this.finish(1);
        }
    }

    private launchTask(request: Request, command: string, args: string[], env: NodeJS.ProcessEnv): void {
        const config = request.arguments!;
        this.taskRunId = `${Date.now()}:${++nextRunTask}`;
        this.taskLaunch = request;
        this.taskDone = new Promise(resolve => { this.taskFinished = resolve; });
        const belongsToRun = (execution: vscode.TaskExecution) =>
            execution.task.definition.type === 'roslynsense-run' &&
            execution.task.definition.runId === this.taskRunId;
        // ProcessExecution gives the program a real terminal (including Console.ReadKey),
        // starts the prepared executable without rebuilding, and reports its PID directly.
        this.taskListeners.push(
            vscode.tasks.onDidStartTaskProcess(event => {
                if (!belongsToRun(event.execution) || this.ended) { return; }
                this.taskExecution = event.execution;
                this.event('process', { name: config.program, systemProcessId: event.processId,
                    isLocalProcess: true, startMethod: 'launch' });
                if (!this.taskStopRequested) { this.replyTaskLaunch(); }
            }),
            vscode.tasks.onDidEndTaskProcess(event => {
                if (!belongsToRun(event.execution)) { return; }
                this.replyTaskLaunch('The terminal process exited before launch completed.');
                this.finish(event.exitCode ?? 1);
            }),
            // A task cancelled or failing before its process starts has no process-end event.
            vscode.tasks.onDidEndTask(event => {
                if (!belongsToRun(event.execution)) { return; }
                this.replyTaskLaunch('The terminal task ended before its process started.');
                this.finish(this.taskStopRequested ? 0 : 1);
            })
        );
        try {
            const taskEnv: Record<string, string> = {};
            for (const [key, value] of Object.entries(env)) {
                if (value !== undefined) { taskEnv[key] = value; }
            }
            const task = new vscode.Task(
                { type: 'roslynsense-run', runId: this.taskRunId }, vscode.TaskScope.Workspace,
                config.name ?? 'Run without debugging', 'RoslynSense',
                new vscode.ProcessExecution(command, args, { cwd: config.cwd, env: taskEnv }), []
            );
            task.presentationOptions = { reveal: vscode.TaskRevealKind.Always,
                panel: vscode.TaskPanelKind.Dedicated, focus: true, echo: false, showReuseMessage: false };
            this.taskCreated = Promise.resolve(vscode.tasks.executeTask(task));
            void this.taskCreated.then(execution => { this.taskExecution = execution; }, error => {
                this.replyTaskLaunch(String(error));
                this.finish(1);
            });
        } catch (error) {
            this.replyTaskLaunch(String(error));
            this.finish(1);
        }
    }

    private replyTaskLaunch(error?: string): void {
        if (!this.taskLaunch) { return; }
        const request = this.taskLaunch;
        this.taskLaunch = undefined;
        this.reply(request, {}, error);
    }

    private stop(): Promise<void> {
        return this.stopping ??= this.stopProcess();
    }

    private async stopProcess(): Promise<void> {
        if (this.taskRunId) {
            this.taskStopRequested = true;
            // A disconnect can arrive while executeTask is still allocating its terminal.
            const execution = this.taskExecution ?? await this.taskCreated?.catch(() => undefined);
            if (!this.ended) { execution?.terminate(); }
            // Reply only after the task process ended, so Restart cannot race the old app.
            await this.taskDone;
            return;
        }
        const child = this.child;
        if (!child?.pid || child.exitCode !== null || child.signalCode !== null) { this.finish(0); return; }
        const closed = new Promise<void>(resolve => child.once('close', () => resolve()));
        if (process.platform === 'win32') {
            // Kill only the process tree this adapter started, so web-server children do not
            // hold the port/output files open when VS Code restarts the run.
            await new Promise<void>((resolve, reject) => {
                const killer = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
                killer.once('error', reject);
                killer.once('close', code => {
                    if (code && child.exitCode === null && child.signalCode === null) {
                        reject(new Error(`Could not stop process ${child.pid} (taskkill exit ${code}).`));
                    } else { resolve(); }
                });
            });
        } else { child.kill('SIGKILL'); }
        await closed;
    }

    private finish(exitCode: number): void {
        if (this.ended) { return; }
        this.ended = true;
        for (const listener of this.taskListeners.splice(0)) { listener.dispose(); }
        this.event('exited', { exitCode });
        this.event('terminated');
        this.taskFinished?.();
    }

    private reply(request: Request, body = {}, error?: string): void {
        this.messages.fire({ seq: ++this.sequence, type: 'response', request_seq: request.seq,
            command: request.command, success: !error, body, ...(error ? { message: error } : {}) } as vscode.DebugProtocolMessage);
    }

    private event(event: string, body = {}): void {
        this.messages.fire({ seq: ++this.sequence, type: 'event', event, body } as vscode.DebugProtocolMessage);
    }

    dispose(): void {
        void this.stop().catch(() => undefined).finally(() => this.messages.dispose());
    }
}
