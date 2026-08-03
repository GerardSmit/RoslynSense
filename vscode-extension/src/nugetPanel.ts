import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * NuGet package management, Rider-style: Browse / Installed / Updates / Consolidate, a
 * versions dropdown, and a details pane — more than a QuickPick can express.
 *
 * All network access happens in the daemon, never here: private feeds need NuGet.config
 * credentials and credential providers, which a webview cannot supply. The webview therefore
 * runs under a strict CSP with no remote content at all — even package icons arrive as data
 * URIs proxied by the server.
 */

interface PackageSummary {
    id: string;
    version: string;
    authors: string | null;
    description: string | null;
    downloads: number | null;
    iconDataUri: string | null;
    deprecated: boolean;
    vulnerable: boolean;
    installedVersion: string | null;
}

interface ProjectPackages {
    projectPath: string;
    projectName: string;
    packages: PackageSummary[];
}

interface Consolidation {
    id: string;
    versions: { projectPath: string; projectName: string; version: string }[];
}

const VIEW_TYPE = 'roslynSense.nuget';

export function registerNuGetPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined
): void {
    let panel: vscode.WebviewPanel | undefined;

    const open = (scopeProject?: string, selectPackage?: string) => {
        if (panel) {
            panel.reveal();
        } else {
            panel = createPanel(context, getClient, () => (panel = undefined));
        }
        if (scopeProject) {
            void panel.webview.postMessage({
                type: 'scope',
                projectPath: scopeProject,
                selectPackage: selectPackage ?? null,
            });
        }
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('roslynSense.manageNuGet', () => open()),
        vscode.commands.registerCommand(
            'roslynSense.manageNuGetForProject',
            (node: { id?: string }, selectPackage?: string) =>
                open(
                    node?.id?.startsWith('project:') ? node.id.slice('project:'.length) : undefined,
                    selectPackage
                )
        ),
        vscode.window.registerWebviewPanelSerializer(VIEW_TYPE, {
            async deserializeWebviewPanel(restored) {
                panel = restored;
                wire(context, restored, getClient, () => (panel = undefined));
            },
        })
    );
}

function createPanel(
    context: vscode.ExtensionContext,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void
): vscode.WebviewPanel {
    const panel = vscode.window.createWebviewPanel(
        VIEW_TYPE,
        'NuGet',
        vscode.ViewColumn.Active,
        { enableScripts: true, retainContextWhenHidden: true }
    );
    wire(context, panel, getClient, onDispose);
    return panel;
}

function wire(
    context: vscode.ExtensionContext,
    panel: vscode.WebviewPanel,
    getClient: () => LanguageClient | undefined,
    onDispose: () => void
): void {
    panel.webview.html = html(panel.webview);
    panel.onDidDispose(onDispose, null, context.subscriptions);

    panel.webview.onDidReceiveMessage(async (message: { type: string; [key: string]: unknown }) => {
        const client = getClient();
        if (!client) {
            void panel.webview.postMessage({ type: 'error', message: 'RoslynSense is not running.' });
            return;
        }

        try {
            switch (message.type) {
                case 'ready':
                case 'projects': {
                    const projects = await client.sendRequest<ProjectPackages[]>(
                        'roslynSense/nuget/installed', {});
                    void panel.webview.postMessage({ type: 'projects', projects });
                    break;
                }
                case 'search': {
                    const results = await client.sendRequest<PackageSummary[]>(
                        'roslynSense/nuget/search',
                        {
                            query: message.query,
                            includePrerelease: message.includePrerelease,
                            skip: message.skip ?? 0,
                            take: 30,
                        }
                    );
                    void panel.webview.postMessage({ type: 'results', results, tab: 'browse' });
                    break;
                }
                case 'updates': {
                    const results = await client.sendRequest<PackageSummary[]>(
                        'roslynSense/nuget/updates',
                        { includePrerelease: message.includePrerelease }
                    );
                    void panel.webview.postMessage({ type: 'results', results, tab: 'updates' });
                    break;
                }
                case 'consolidations': {
                    const results = await client.sendRequest<Consolidation[]>(
                        'roslynSense/nuget/consolidations', {});
                    void panel.webview.postMessage({ type: 'consolidations', results });
                    break;
                }
                case 'versions': {
                    const versions = await client.sendRequest<string[]>(
                        'roslynSense/nuget/versions',
                        { id: message.id, includePrerelease: message.includePrerelease }
                    );
                    void panel.webview.postMessage({ type: 'versions', id: message.id, versions });
                    break;
                }
                case 'install':
                case 'update':
                case 'uninstall': {
                    await vscode.window.withProgress(
                        {
                            location: vscode.ProgressLocation.Notification,
                            title: `${message.type === 'uninstall' ? 'Removing' : 'Installing'} ${message.id}…`,
                        },
                        () =>
                            client.sendRequest(`roslynSense/nuget/${message.type}`, {
                                id: message.id,
                                version: message.version ?? null,
                                projectPaths: message.projectPaths ?? [],
                            })
                    );
                    void panel.webview.postMessage({ type: 'refresh' });
                    break;
                }
                case 'openExternal': {
                    // Links open in the real browser; the webview itself never navigates.
                    await vscode.env.openExternal(vscode.Uri.parse(String(message.url)));
                    break;
                }
            }
        } catch (error) {
            void panel.webview.postMessage({
                type: 'error',
                message: error instanceof Error ? error.message : String(error),
            });
        }
    }, null, context.subscriptions);
}

function html(webview: vscode.Webview): string {
    const nonce = Array.from({ length: 32 }, () =>
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.charAt(
            Math.floor(Math.random() * 62)
        )
    ).join('');

    // img-src allows data: only — icons are proxied by the server, nothing is fetched here.
    const csp = [
        "default-src 'none'",
        `img-src data: ${webview.cspSource}`,
        `style-src 'unsafe-inline' ${webview.cspSource}`,
        `script-src 'nonce-${nonce}'`,
    ].join('; ');

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<style>
  body {
    margin: 0; padding: 0;
    font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
    color: var(--vscode-foreground); background: var(--vscode-editor-background);
    display: flex; flex-direction: column; height: 100vh;
  }
  header { padding: 8px; border-bottom: 1px solid var(--vscode-panel-border); display: flex; gap: 8px; flex-wrap: wrap; }
  input[type="search"], select {
    background: var(--vscode-input-background); color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent); padding: 4px 6px; border-radius: 2px;
  }
  input[type="search"] { flex: 1 1 240px; }
  nav { display: flex; gap: 4px; padding: 0 8px; border-bottom: 1px solid var(--vscode-panel-border); }
  nav button {
    background: none; border: none; border-bottom: 2px solid transparent;
    color: var(--vscode-foreground); padding: 6px 10px; cursor: pointer;
  }
  nav button[aria-selected="true"] { border-bottom-color: var(--vscode-focusBorder); font-weight: 600; }
  main { display: flex; flex: 1; min-height: 0; }
  ul { list-style: none; margin: 0; padding: 0; overflow-y: auto; flex: 1 1 50%; border-right: 1px solid var(--vscode-panel-border); }
  li { display: flex; gap: 8px; padding: 8px; cursor: pointer; align-items: flex-start; }
  li[aria-selected="true"], li:hover { background: var(--vscode-list-hoverBackground); }
  li img { width: 32px; height: 32px; }
  .id { font-weight: 600; }
  .muted { color: var(--vscode-descriptionForeground); font-size: 0.9em; }
  section.details { flex: 1 1 50%; overflow-y: auto; padding: 12px; }
  button.action {
    background: var(--vscode-button-background); color: var(--vscode-button-foreground);
    border: none; padding: 6px 12px; border-radius: 2px; cursor: pointer;
  }
  .banner { padding: 6px 8px; border-radius: 2px; margin-bottom: 8px; }
  .banner.warn { background: var(--vscode-inputValidation-warningBackground); }
  .banner.error { background: var(--vscode-inputValidation-errorBackground); }
  a { color: var(--vscode-textLink-foreground); cursor: pointer; }
  :focus-visible { outline: 1px solid var(--vscode-focusBorder); outline-offset: 2px; }
</style>
</head>
<body>
<header>
  <input type="search" id="query" placeholder="Search packages…  (press / to focus)" aria-label="Search packages">
  <label><input type="checkbox" id="prerelease"> Prerelease</label>
  <select id="scope" aria-label="Projects"><option value="">All projects</option></select>
</header>
<nav role="tablist">
  <button role="tab" data-tab="browse" aria-selected="true">Browse</button>
  <button role="tab" data-tab="installed" aria-selected="false">Installed</button>
  <button role="tab" data-tab="updates" aria-selected="false">Updates</button>
  <button role="tab" data-tab="consolidate" aria-selected="false">Consolidate</button>
</nav>
<main>
  <ul id="list" role="listbox" aria-label="Packages"></ul>
  <section class="details" id="details" aria-live="polite"><p class="muted">Select a package.</p></section>
</main>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
const state = { tab: 'browse', packages: [], projects: [], selected: null, versions: {}, pendingSelect: null };

const el = (id) => document.getElementById(id);
const post = (message) => vscode.postMessage(message);

function render() {
  const list = el('list');
  list.textContent = '';
  state.packages.forEach((pkg, index) => {
    const li = document.createElement('li');
    li.setAttribute('role', 'option');
    li.setAttribute('aria-selected', String(state.selected === index));
    li.tabIndex = 0;
    if (pkg.iconDataUri) {
      const img = document.createElement('img');
      img.src = pkg.iconDataUri;
      img.alt = '';
      li.appendChild(img);
    }
    const text = document.createElement('div');
    const id = document.createElement('div');
    id.className = 'id';
    id.textContent = pkg.id + (pkg.installedVersion ? '  · installed ' + pkg.installedVersion : '');
    const meta = document.createElement('div');
    meta.className = 'muted';
    meta.textContent = [pkg.authors, pkg.version, pkg.downloads ? pkg.downloads.toLocaleString() + ' downloads' : null]
      .filter(Boolean).join(' · ');
    text.appendChild(id);
    text.appendChild(meta);
    if (pkg.description) {
      const description = document.createElement('div');
      description.className = 'muted';
      description.textContent = pkg.description;
      text.appendChild(description);
    }
    li.appendChild(text);
    li.addEventListener('click', () => select(index));
    li.addEventListener('keydown', (e) => { if (e.key === 'Enter') select(index); });
    list.appendChild(li);
  });
}

function select(index) {
  state.selected = index;
  const pkg = state.packages[index];
  if (!pkg) return;
  post({ type: 'versions', id: pkg.id, includePrerelease: el('prerelease').checked });
  renderDetails(pkg);
  render();
}

function renderDetails(pkg) {
  const details = el('details');
  details.textContent = '';

  if (pkg.vulnerable) details.appendChild(banner('error', 'This package has known vulnerabilities.'));
  if (pkg.deprecated) details.appendChild(banner('warn', 'This package is deprecated.'));

  const title = document.createElement('h2');
  title.textContent = pkg.id;
  details.appendChild(title);

  const row = document.createElement('div');
  const versions = document.createElement('select');
  versions.id = 'versions';
  (state.versions[pkg.id] || [pkg.version]).forEach((v) => {
    const option = document.createElement('option');
    option.value = v; option.textContent = v;
    versions.appendChild(option);
  });
  row.appendChild(versions);

  const install = document.createElement('button');
  install.className = 'action';
  install.textContent = pkg.installedVersion ? 'Update' : 'Install';
  install.addEventListener('click', () => post({
    type: pkg.installedVersion ? 'update' : 'install',
    id: pkg.id, version: versions.value, projectPaths: scopeProjects(),
  }));
  row.appendChild(install);

  if (pkg.installedVersion) {
    const remove = document.createElement('button');
    remove.className = 'action';
    remove.textContent = 'Uninstall';
    remove.addEventListener('click', () => post({
      type: 'uninstall', id: pkg.id, projectPaths: scopeProjects(),
    }));
    row.appendChild(remove);
  }
  details.appendChild(row);

  if (pkg.description) {
    const description = document.createElement('p');
    description.textContent = pkg.description;
    details.appendChild(description);
  }
}

function banner(kind, text) {
  const div = document.createElement('div');
  div.className = 'banner ' + kind;
  div.textContent = text;
  return div;
}

function scopeProjects() {
  const value = el('scope').value;
  return value ? [value] : state.projects.map((p) => p.projectPath);
}

function applyPendingSelection() {
  if (!state.pendingSelect) return;
  const index = state.packages.findIndex((p) => p.id.toLowerCase() === state.pendingSelect.toLowerCase());
  state.pendingSelect = null;
  if (index >= 0) select(index);
}

function switchTab(tab) {
  state.tab = tab;
  document.querySelectorAll('nav button').forEach((b) =>
    b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  if (tab === 'browse') post({ type: 'search', query: el('query').value, includePrerelease: el('prerelease').checked });
  if (tab === 'updates') post({ type: 'updates', includePrerelease: el('prerelease').checked });
  if (tab === 'consolidate') post({ type: 'consolidations' });
  if (tab === 'installed') post({ type: 'projects' });
}

document.querySelectorAll('nav button').forEach((b) =>
  b.addEventListener('click', () => switchTab(b.dataset.tab)));

let debounce;
el('query').addEventListener('input', () => {
  clearTimeout(debounce);
  debounce = setTimeout(() => switchTab('browse'), 300);
});

document.addEventListener('keydown', (e) => {
  if (e.key === '/' && document.activeElement !== el('query')) {
    e.preventDefault();
    el('query').focus();
  }
});

window.addEventListener('message', (event) => {
  const message = event.data;
  if (message.type === 'results') {
    state.packages = message.results;
    state.selected = null;
    render();
  } else if (message.type === 'projects') {
    state.projects = message.projects;
    const scope = el('scope');
    scope.textContent = '';
    const all = document.createElement('option');
    all.value = ''; all.textContent = 'All projects';
    scope.appendChild(all);
    message.projects.forEach((p) => {
      const option = document.createElement('option');
      option.value = p.projectPath; option.textContent = p.projectName;
      scope.appendChild(option);
    });
    if (state.tab === 'installed') {
      state.packages = message.projects.flatMap((p) => p.packages);
      render();
      applyPendingSelection();
    }
  } else if (message.type === 'scope') {
    // Opened from a Dependencies or package node in the Solution Explorer: scope to that
    // project, and open on the package that was clicked if there was one.
    state.pendingSelect = message.selectPackage || null;
    el('scope').value = message.projectPath || '';
    switchTab(message.selectPackage ? 'installed' : state.tab);
  } else if (message.type === 'versions') {
    state.versions[message.id] = message.versions;
    const pkg = state.packages[state.selected];
    if (pkg && pkg.id === message.id) renderDetails(pkg);
  } else if (message.type === 'consolidations') {
    state.packages = message.results.map((c) => ({
      id: c.id, version: c.versions.map((v) => v.version).join(', '),
      authors: null, description: c.versions.map((v) => v.projectName + ': ' + v.version).join(' · '),
      downloads: null, iconDataUri: null, deprecated: false, vulnerable: false, installedVersion: null,
    }));
    render();
  } else if (message.type === 'refresh') {
    switchTab(state.tab);
  } else if (message.type === 'error') {
    el('details').textContent = '';
    el('details').appendChild(banner('error', message.message));
  }
});

post({ type: 'ready' });
</script>
</body>
</html>`;
}
