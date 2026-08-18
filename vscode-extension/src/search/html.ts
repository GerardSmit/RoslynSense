import * as vscode from 'vscode';

/**
 * The panel's document shell. Static markup only — every result row is built by the webview
 * script from `document.createElement`, so no server or file text is ever interpolated into HTML.
 *
 * The layout mirrors Rider's popup: a query row, a tab strip, a growing results list, and a
 * preview pane under it with the selection's path and an "Open in Right Split" affordance.
 */
export function html(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const nonce = Array.from({ length: 32 }, () =>
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.charAt(
            Math.floor(Math.random() * 62)
        )
    ).join('');

    const script = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'out', 'webview', 'search.js')
    );
    const style = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'search.css'));

    const csp = [
        "default-src 'none'",
        `style-src 'unsafe-inline' ${webview.cspSource}`,
        `script-src 'nonce-${nonce}'`,
    ].join('; ');

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<link rel="stylesheet" href="${style}">
</head>
<body>

<header class="chrome">
  <div class="query-row">
    <input type="text" id="query" placeholder="Search types, members, files, actions and text — Namespace.Type.Member narrows"
           aria-label="Search everywhere" autocomplete="off" spellcheck="false">
    <label class="check" title="Also search public types of referenced assemblies (opens decompiled source) — Alt+N or double Shift">
      <input type="checkbox" id="non-solution"> Include non-solution items
    </label>
  </div>
  <nav role="tablist" aria-label="Result kinds">
    <button role="tab" data-tab="all" aria-selected="true">All</button>
    <button role="tab" data-tab="classes" aria-selected="false">Classes</button>
    <button role="tab" data-tab="files" aria-selected="false">Files</button>
    <button role="tab" data-tab="symbols" aria-selected="false">Symbols</button>
    <button role="tab" data-tab="actions" aria-selected="false">Actions</button>
    <button role="tab" data-tab="text" aria-selected="false">Text</button>
    <span class="hint">Tab switches · ↑↓ selects · Enter opens · Ctrl+Enter opens in right split</span>
  </nav>
  <div class="progress" id="progress" role="presentation"></div>
</header>

<ul id="results" role="listbox" tabindex="-1" aria-label="Search results"></ul>

<div id="splitter" role="separator" aria-orientation="horizontal" aria-label="Resize the preview"></div>

<section id="preview" aria-label="Preview" aria-live="polite">
  <pre id="preview-code"><code id="preview-lines"></code></pre>
</section>

<footer class="status">
  <span id="status-path" class="truncate"></span>
  <span class="spacer"></span>
  <button class="linklike" id="open-split" hidden>Open in Right Split</button>
</footer>

<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
}
