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
        return; // no queue directory — nothing pending
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
