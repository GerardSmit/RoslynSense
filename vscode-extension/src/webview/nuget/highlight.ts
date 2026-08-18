/// <reference path="./dom.ts" />

/**
 * Syntax colouring for fenced code in a README.
 *
 * Hand-written and small, for the same reason the markdown renderer is: the panel has no bundler
 * (`tsconfig.webview.json` is `module: none` with an `outFile`, so the script is concatenated by
 * `tsc`), and a highlighter you can read end to end is one you can be sure never builds a node from
 * anything but the fixed tag list below. Every token becomes a `<span>` with a class; nothing here
 * can emit an attribute, a URL or another element.
 *
 * The languages covered are the ones .NET READMEs actually contain — C#, project/config XML, JSON
 * and shell transcripts. Anything else, including a fence with no language, renders exactly as it
 * did before: plain text. Being obviously unstyled is a better failure than being confidently
 * miscoloured.
 *
 * This approximates the editor's colours rather than matching them. VS Code does not expose theme
 * token colours to a webview, so the CSS uses the chart palette, which is themed and guaranteed to
 * exist.
 */
namespace NG {
    type Rule = { cls: string; re: string };

    /**
     * Order is precedence: whichever rule matches earliest wins, and at the same position the
     * rule listed first wins. Comments and strings therefore come before everything, or a keyword
     * inside a string would be coloured as a keyword.
     */
    const CSharp: Rule[] = [
        { cls: 'com', re: String.raw`//[^\n]*|/\*[\s\S]*?\*/` },
        { cls: 'str', re: String.raw`@"(?:[^"]|"")*"|\$?"(?:\\[\s\S]|[^"\\\n])*"|'(?:\\[\s\S]|[^'\\\n])'` },
        { cls: 'pre', re: String.raw`^[ \t]*#[ \t]*\w+` },
        {
            cls: 'kw',
            re: String.raw`\b(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|global|goto|if|implicit|in|init|int|interface|internal|is|lock|long|namespace|new|nameof|null|object|operator|out|override|params|private|protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|when|where|while|yield)\b`,
        },
        { cls: 'num', re: String.raw`\b\d(?:[\d_]*\.?[\d_]*)(?:[eE][+-]?\d+)?[fFdDmMuUlL]*\b|\b0[xXbB][\da-fA-F_]+\b` },
        { cls: 'typ', re: String.raw`\b[A-Z][A-Za-z0-9_]*\b` },
    ];

    const Xml: Rule[] = [
        { cls: 'com', re: String.raw`<!--[\s\S]*?-->` },
        { cls: 'str', re: String.raw`"[^"]*"|'[^']*'` },
        { cls: 'tag', re: String.raw`</?[A-Za-z_][\w.:-]*|/?>` },
        { cls: 'attr', re: String.raw`[A-Za-z_][\w.:-]*(?=\s*=)` },
    ];

    const Json: Rule[] = [
        { cls: 'com', re: String.raw`//[^\n]*|/\*[\s\S]*?\*/` },
        { cls: 'attr', re: String.raw`"(?:\\[\s\S]|[^"\\])*"(?=\s*:)` },
        { cls: 'str', re: String.raw`"(?:\\[\s\S]|[^"\\])*"` },
        { cls: 'kw', re: String.raw`\b(?:true|false|null)\b` },
        { cls: 'num', re: String.raw`-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b` },
    ];

    const Shell: Rule[] = [
        { cls: 'com', re: String.raw`#[^\n]*` },
        { cls: 'str', re: String.raw`"(?:\\[\s\S]|[^"\\])*"|'[^']*'` },
        { cls: 'kw', re: String.raw`^[ \t]*[>$]?[ \t]*[\w.-]+` },
        { cls: 'attr', re: String.raw`(?<=\s)--?[A-Za-z][\w-]*` },
    ];

    const Languages: Record<string, Rule[]> = {
        cs: CSharp, csharp: CSharp, 'c#': CSharp,
        xml: Xml, html: Xml, xaml: Xml, csproj: Xml, props: Xml, targets: Xml, config: Xml, axml: Xml,
        json: Json, jsonc: Json,
        sh: Shell, bash: Shell, shell: Shell, console: Shell, zsh: Shell,
        powershell: Shell, pwsh: Shell, ps: Shell, ps1: Shell, cmd: Shell, batch: Shell, dotnetcli: Shell,
    };

    /** Compiled once per language: building the combined pattern per fence is pure waste. */
    const compiled = new Map<Rule[], RegExp>();

    /**
     * The fence's info string reduced to a language key. Fences carry things like
     * `csharp title="Program.cs"`, and only the first word is the language.
     */
    export function fenceLanguage(info: string): string {
        return info.trim().split(/[\s,{]/)[0].replace(/^\./, '').toLowerCase();
    }

    export function highlight(code: string, language: string): Node {
        const rules = Languages[language];
        if (!rules) {
            return document.createTextNode(code);
        }

        let pattern = compiled.get(rules);
        if (!pattern) {
            // One group per rule, so the index of the group that matched names the class. The
            // rule sources must therefore contain no capturing groups of their own.
            pattern = new RegExp(rules.map((rule) => `(${rule.re})`).join('|'), 'gm');
            compiled.set(rules, pattern);
        }

        const fragment = document.createDocumentFragment();
        let last = 0;
        pattern.lastIndex = 0;

        for (const match of code.matchAll(pattern)) {
            const index = rules.findIndex((_, i) => match[i + 1] !== undefined);
            if (index < 0) {
                continue;
            }

            if (match.index > last) {
                fragment.appendChild(document.createTextNode(code.slice(last, match.index)));
            }
            fragment.appendChild(make('span', `tok tok-${rules[index].cls}`, match[0]));
            last = match.index + match[0].length;
        }

        if (last < code.length) {
            fragment.appendChild(document.createTextNode(code.slice(last)));
        }
        return fragment;
    }
}
