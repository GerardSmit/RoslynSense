// Checks the markup projection that feeds JavaScript and CSS IntelliSense — see
// src/embeddedProjection.ts. Runs on the compiled output under plain node, no editor involved:
//     npm run check:embedded
//
// What it is guarding is that a position means the same thing in the page and in the projection,
// and that server code never reaches a language service that would read it as JavaScript.

import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import * as path from 'node:path';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));

let projection;
try {
    projection = require(path.join(here, '..', 'out', 'embeddedProjection.js'));
} catch {
    console.error('out/embeddedProjection.js is missing — run `npm run compile` first.');
    process.exit(2);
}

const { project, regions } = projection;

let failures = 0;
function check(name, condition, detail) {
    if (condition) {
        console.log(`  ok   ${name}`);
    } else {
        failures++;
        console.log(`  FAIL ${name}${detail ? '\n       ' + detail : ''}`);
    }
}

const page = [
    '<%@ Control Language="C#" Inherits="Site.Thing" %>',
    '<script runat="server">',
    '    protected void Page_Load(object sender, EventArgs e) { var x = "</div>"; }',
    '</script>',
    '<style>',
    '    .card { color: red; }',
    '</style>',
    '<script type="text/template">',
    '    <div>{{name}}</div>',
    '</script>',
    '<script>',
    '    var url = "<%= ResolveUrl("~/a") %>";',
    '    document.querySelector(".card");',
    '</script>',
    '<div onclick="if (a > b) f()">text</div>',
    '<%-- a comment with <script> in it --%>',
].join('\n');

const projected = project(page);

console.log('projection');
check('length is unchanged', projected.length === page.length,
    `${projected.length} vs ${page.length}`);
check('line count is unchanged', projected.split('\n').length === page.split('\n').length);
check('the directive is gone', !projected.includes('Inherits'));
check('the server script body is gone', !projected.includes('Page_Load'));
check('the server script tags are gone', !/<script runat/.test(projected));
check('the server expression is gone', !projected.includes('ResolveUrl'));
check('the client script survives', projected.includes('document.querySelector(".card")'));
check('the string that held an expression still closes', /var url = " *";/.test(projected));
check('the style block survives', projected.includes('.card { color: red; }'));
check('the server comment is gone', !projected.includes('a comment with'));

console.log('regions');
const found = regions(page).sort((a, b) => a.start - b.start);
const at = (offset) => found.find((r) => offset >= r.start && offset <= r.end);

check('the server script is not a region', at(page.indexOf('Page_Load')) === undefined);
check('the style block is css', at(page.indexOf('color: red'))?.kind === 'css');
check('a text/template script is not a region', at(page.indexOf('{{name}}')) === undefined);
check('the client script is javascript',
    at(page.indexOf('document.querySelector'))?.kind === 'javascript');
check('plain markup is not a region', at(page.indexOf('<div onclick')) === undefined);

// The one in the server comment is the reason regions are scanned over the projection.
check('exactly two regions', found.length === 2, `${found.length}: ${JSON.stringify(found)}`);

console.log('edge cases');
check('an attribute holding > does not end the tag early',
    project('<script data-cond="a > b">var a = 1;</script>').includes('var a = 1;'));

// A server script named inside a comment is not an element. Pairing that phantom open tag with
// the next real `</script>` blanked every line between them, and the live block vanished.
const commented = [
    '<%-- <script runat="server"> old code --%>',
    '<script>var alive = 1;</script>',
].join('\n');
check('a server script inside a comment does not swallow the next real one',
    project(commented).includes('var alive = 1;'),
    JSON.stringify(project(commented)));
check('and that block is still a region', regions(commented).length === 1,
    JSON.stringify(regions(commented)));

const quoted = [
    '<% Response.Write("<script runat=server>"); %>',
    '<script>var survivor = 2;</script>',
].join('\n');
check('a server script quoted in a code block does not swallow the next real one',
    project(quoted).includes('var survivor = 2;'),
    JSON.stringify(project(quoted)));
check('runat with no quotes is still server',
    regions('<script runat=server>x</script>').length === 0);
check('an unterminated script does not run away',
    regions('<script>let a =').length === 1);
check('an unterminated server island blanks to the end',
    !project('<div><% oops').includes('oops'));
check('a self-closing script is skipped',
    regions('<script src="a.js" />after').length === 0);

console.log(failures === 0 ? '\nALL PASS' : `\n${failures} FAILED`);
process.exit(failures === 0 ? 0 : 1);
