import * as vscode from 'vscode';

/**
 * The panel's document shell. Static markup only — every row is built by the webview script from
 * the schema, so no file contents are ever interpolated into HTML.
 */
export function html(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const nonce = Array.from({ length: 32 }, () =>
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.charAt(
            Math.floor(Math.random() * 62)
        )
    ).join('');

    const script = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'out', 'webview', 'settings.js')
    );
    const style = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'settings.css'));

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
  <div class="scope-row">
    <span class="scope-label">Editing</span>
    <nav id="scopes" role="tablist" aria-label="Settings scope"></nav>
  </div>
  <p id="scope-file" class="scope-file"></p>
  <div class="search-row">
    <input id="search" type="search" placeholder="Search settings" aria-label="Search settings">
  </div>
  <p id="notice" class="notice" role="status" hidden></p>
</header>

<main id="form" aria-label="RoslynSense settings"></main>
<p id="no-matches" class="no-matches" hidden></p>

<footer class="status">
  <span class="hint">Values shown are the ones in effect. Later scopes override earlier ones,
  field by field — a note under a setting says when its value comes from another scope.</span>
</footer>

<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
}
