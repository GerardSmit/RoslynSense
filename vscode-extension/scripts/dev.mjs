import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// VS Code does not resolve a relative --extensionDevelopmentPath, so it has to be absolute, and
// an npm script cannot produce one portably. The folder to open is forwarded from the command
// line: `npm run dev -- <folder>`, defaulting to the repo this extension lives in.
const extension = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repository = resolve(extension, '..');
const target = process.argv.slice(2);

// A shell is unavoidable — on Windows `code` and `dotnet` resolve through .cmd shims, which node
// refuses to spawn without one — so commands are assembled quoted rather than handed over as an
// array for the shell to concatenate, which is what a path containing a space would otherwise fall
// apart on.
const quote = (argument) => (/[\s"]/.test(argument) ? `"${argument.replaceAll('"', '\\"')}"` : argument);

/**
 * The server this window should talk to.
 *
 * The extension resolves `roslyn-sense` from PATH, which is the globally installed tool — so
 * without this a dev run silently exercises whatever was last `dotnet tool install`ed and a change
 * you just made appears not to have happened. Building here and pointing the extension at the
 * output is what makes `npm run dev` mean "run what is in this working tree".
 *
 * `ROSLYNSENSE_SERVER=… npm run dev` still wins, and `--no-server-build` skips the build for a
 * fast restart when only the TypeScript changed.
 */
function devServer() {
    if (process.env.ROSLYNSENSE_SERVER?.trim()) {
        console.log(`server: ROSLYNSENSE_SERVER=${process.env.ROSLYNSENSE_SERVER}`);
        return undefined;
    }

    const project = resolve(repository, 'RoslynMCP', 'RoslynMCP.csproj');
    const built = resolve(repository, 'RoslynMCP', 'bin', 'Debug', 'net10.0', 'RoslynMCP.exe');

    if (!target.includes('--no-server-build')) {
        console.log('building the server…');
        const build = spawnSync(['dotnet', 'build', quote(project), '-v', 'q', '--nologo'].join(' '), {
            stdio: 'inherit',
            shell: true,
        });

        // A failed build is not a reason to refuse to open the editor — the window is still useful
        // for TypeScript work — but it must not look like it succeeded either.
        if (build.status !== 0)
            console.warn('server build failed; falling back to roslyn-sense on PATH');
    }

    if (existsSync(built)) {
        console.log(`server: ${built}`);
        return built;
    }

    console.warn(`server: roslyn-sense on PATH (${built} does not exist)`);
    return undefined;
}

const server = devServer();

// C# Dev Kit and the Microsoft C# extension register the same providers, so results appear twice
// and the two language servers fight over the solution.
const folders = target.filter((argument) => argument !== '--no-server-build');
const args = [
    `--extensionDevelopmentPath=${extension}`,
    '--disable-extension', 'ms-dotnettools.csdevkit',
    '--disable-extension', 'ms-dotnettools.csharp',
    ...(folders.length > 0 ? folders : [repository]),
];

const command = ['code', ...args].map(quote).join(' ');

console.log(command);
spawn(command, {
    stdio: 'inherit',
    shell: true,
    env: server ? { ...process.env, ROSLYNSENSE_SERVER: server } : process.env,
});
