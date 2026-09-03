import * as vscode from 'vscode';

/**
 * The panel's document shell. Static markup only — every row is built by the webview script from
 * what the server answered, so no path, namespace or file name is ever interpolated into HTML.
 */
export function html(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const nonce = Array.from({ length: 32 }, () =>
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.charAt(
            Math.floor(Math.random() * 62)
        )
    ).join('');

    const script = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'out', 'webview', 'properties.js')
    );
    const style = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'media', 'properties.css')
    );

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
  <h1 id="title"></h1>
  <p id="subtitle" class="subtitle"></p>
</header>

<main id="form" aria-label="Properties"></main>

<footer class="status">
  <p id="notice" class="notice" role="status"></p>
</footer>

<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
}
