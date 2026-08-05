import * as vscode from 'vscode';

/**
 * The panel's document shell.
 *
 * The markup is deliberately static — every list row, banner and details section is built by the
 * webview script from `document.createElement`, so no package text is ever interpolated into HTML.
 *
 * The layout is a single flex column whose one growing child is the tab body. Each tab owns a
 * `<section class="pane">`, and only the active one is shown: mixing them into one scroll region
 * is what put a feed list under the package list.
 */
export function html(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const nonce = Array.from({ length: 32 }, () =>
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.charAt(
            Math.floor(Math.random() * 62)
        )
    ).join('');

    const script = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'out', 'webview', 'nuget.js')
    );
    const style = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'nuget.css'));

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
<link rel="stylesheet" href="${style}">
</head>
<body>

<header class="chrome">
  <div class="search-row">
    <div class="search-field">
      <input type="search" id="query" placeholder="Search packages…" aria-label="Search packages">
      <kbd class="hint" aria-hidden="true">/</kbd>
    </div>
    <label class="check"><input type="checkbox" id="prerelease"> Prerelease</label>
    <select id="source" aria-label="Package source"><option value="">All sources</option></select>
  </div>

  <div class="scope-row">
    <button class="chip" id="scope" aria-describedby="scope-summary">Choose projects…</button>
    <span class="muted truncate" id="scope-summary">No project selected.</span>
  </div>

  <nav role="tablist" aria-label="Package views">
    <button role="tab" data-tab="browse" aria-selected="true" id="tab-browse">Browse</button>
    <button role="tab" data-tab="installed" aria-selected="false" id="tab-installed">
      Installed<span class="count" data-count="installed"></span>
    </button>
    <button role="tab" data-tab="updates" aria-selected="false" id="tab-updates">
      Updates<span class="count" data-count="updates"></span>
    </button>
    <button role="tab" data-tab="sources" aria-selected="false" id="tab-sources">Sources</button>
  </nav>
  <div class="progress" id="progress" role="presentation"></div>
</header>

<div class="strip" id="feeds" hidden></div>
<div class="strip" id="summary" hidden></div>

<div class="toolbar" id="installed-toolbar" hidden>
  <button class="chip filter" data-filter="all" aria-pressed="true">All</button>
  <button class="chip filter" data-filter="updates" aria-pressed="false">Updates</button>
  <button class="chip filter" data-filter="mixed" aria-pressed="false"
          title="Projects referencing this package at different versions (Consolidate)">Mixed versions</button>
</div>

<div class="toolbar" id="updates-toolbar" hidden>
  <label class="check"><input type="checkbox" id="select-all"> Select all</label>
  <label class="check">Update to
    <select id="version-lock" aria-label="How far a version may move">
      <option value="none">latest</option>
      <option value="major">same major</option>
      <option value="minor">same minor</option>
    </select>
  </label>
  <span class="muted" id="plan-note" aria-live="polite"></span>
  <span class="spacer"></span>
  <button class="action" id="update-selected" disabled>Update</button>
</div>

<section class="pane" id="pane-packages">
  <ul id="list" role="listbox" tabindex="0" aria-label="Packages"></ul>
  <div id="splitter" role="separator" tabindex="0" aria-orientation="vertical"
       aria-label="Resize the details pane" aria-valuemin="20" aria-valuemax="80" aria-valuenow="42"></div>
  <section class="details" id="details" tabindex="0" aria-label="Package details" aria-live="polite">
    <p class="placeholder">Select a package to see its details.</p>
  </section>
</section>

<section class="pane" id="pane-sources" aria-label="Package sources" hidden>
  <div class="sources-body">
    <div class="toolbar">
      <button class="action" id="source-add">Add feed…</button>
      <button class="linklike" id="source-open-config">Open NuGet.config</button>
      <span class="spacer"></span>
      <span class="muted">Order decides which feed answers first.</span>
    </div>
    <ul id="sources-list" aria-label="Configured feeds"></ul>
    <p class="muted footnote">
      Credentials are kept in the OS keychain, never written to NuGet.config.
    </p>
  </div>
</section>

<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
}
