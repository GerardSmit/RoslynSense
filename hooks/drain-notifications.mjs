// PreToolUse hook: drains RoslynSense's per-solution pending message queue and injects the
// messages as additional context. This is how out-of-band events (the user killing a launched
// app from the editor's status bar, for example) reach the LLM.
//
// The queue directory is %TEMP%/roslyn-sense/notifications/<key>/ where <key> is the first
// 8 bytes (16 hex chars) of SHA-256 over the lowercased absolute solution path — the same
// derivation as HostPaths.Hash / PendingNotificationStore in the RoslynSense server. Solution
// discovery mirrors PathHelper.FindNearestSolution: walk up from cwd, .sln before .slnx.
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

let input = '';
process.stdin.on('data', (chunk) => (input += chunk));
process.stdin.on('end', () => {
    try {
        main(JSON.parse(input || '{}'));
    } catch {
        // A hook must never break tool calls.
    }
});

function findNearestSolution(start) {
    let dir = start;
    try {
        if (fs.statSync(dir).isFile()) dir = path.dirname(dir);
    } catch {
        return null;
    }
    // Case-insensitive sort + files only — must match PathHelper.FindSolutionFiles exactly,
    // or the two sides hash different solutions and notifications are silently lost.
    const ciCompare = (a, b) => {
        const la = a.toLowerCase(), lb = b.toLowerCase();
        return la < lb ? -1 : la > lb ? 1 : 0;
    };
    for (;;) {
        let entries = [];
        try {
            entries = fs
                .readdirSync(dir, { withFileTypes: true })
                .filter((e) => e.isFile())
                .map((e) => e.name);
        } catch {
            /* unreadable dir — keep walking up */
        }
        const sln = entries.filter((e) => e.toLowerCase().endsWith('.sln')).sort(ciCompare);
        const slnx = entries.filter((e) => e.toLowerCase().endsWith('.slnx')).sort(ciCompare);
        const first = [...sln, ...slnx][0];
        if (first) return path.join(dir, first);
        const parent = path.dirname(dir);
        if (parent === dir) return null;
        dir = parent;
    }
}

// The editor's own debug session (written by the daemon on the extension's
// roslynSense/editorDebugState notifications). Injected only when the state CHANGED since
// the last injection — this hook fires on every prompt/tool call, and repeating "user is
// paused at X" each time would drown the conversation.
function editorDebugContext(key) {
    const stateFile = path.join(os.tmpdir(), 'roslyn-sense', 'editor-debug', `${key}.json`);
    let raw;
    try {
        raw = fs.readFileSync(stateFile, 'utf8');
    } catch {
        return null; // no editor debug session
    }

    let state;
    try {
        state = JSON.parse(raw);
    } catch {
        return null;
    }
    if (!state || !state.Active) return null;

    // Ignore stale leftovers (editor crashed without clearing the file).
    const updated = Date.parse(state.UpdatedAtUtc || '');
    if (!Number.isFinite(updated) || Date.now() - updated > 4 * 60 * 60 * 1000) return null;

    const markerFile = stateFile + '.injected';
    const fingerprint = `${state.ExecutionState}|${state.FilePath}|${state.Line}|${state.Reason}`;
    try {
        if (fs.readFileSync(markerFile, 'utf8') === fingerprint) return null; // already told
    } catch {
        /* no marker yet */
    }
    try {
        fs.writeFileSync(markerFile, fingerprint);
    } catch {
        /* advisory */
    }

    if (state.ExecutionState === 'stopped' && state.FilePath) {
        return (
            `The user is debugging in the editor (session '${state.SessionName || 'unknown'}') and is ` +
            `paused at ${state.FilePath}:${state.Line}` +
            (state.Reason ? ` (reason: ${state.Reason})` : '') +
            '. You can inspect and control this session with the debug tools: DebugEvaluate, ' +
            'DebugStatus, DebugContinue (continue/step), DebugSetBreakpoint, DebugRunUntil. ' +
            'Do not stop it — it belongs to the user.'
        );
    }
    return (
        `The user is debugging in the editor (session '${state.SessionName || 'unknown'}', currently running). ` +
        'DebugStatus/DebugSetBreakpoint work against that session.'
    );
}

function main(hook) {
    const cwd = hook.cwd || process.cwd();
    const solution = findNearestSolution(cwd);
    if (!solution) return;

    const key = createHash('sha256')
        .update(path.resolve(solution).toLowerCase(), 'utf8')
        .digest('hex')
        .slice(0, 16);
    const queueDir = path.join(os.tmpdir(), 'roslyn-sense', 'notifications', key);

    let files = [];
    try {
        files = fs.readdirSync(queueDir).filter((f) => f.endsWith('.txt')).sort();
    } catch {
        // No queue directory — nothing queued, but editor-debug context below may still apply.
    }

    const messages = [];
    for (const file of files) {
        const full = path.join(queueDir, file);
        try {
            messages.push(fs.readFileSync(full, 'utf8').trim());
            fs.unlinkSync(full);
        } catch {
            // Mid-write or drained by a concurrent chat — skip.
        }
    }

    const debugMessage = editorDebugContext(key);
    if (debugMessage) messages.push(debugMessage);

    if (messages.length === 0) return;

    // hookEventName must echo the event this invocation is for (the same script serves
    // both UserPromptSubmit and PreToolUse).
    process.stdout.write(
        JSON.stringify({
            hookSpecificOutput: {
                hookEventName: hook.hook_event_name || 'PreToolUse',
                additionalContext: '[RoslynSense] ' + messages.join('\n[RoslynSense] '),
            },
        })
    );
}
