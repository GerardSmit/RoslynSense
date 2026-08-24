using Microsoft.Language.Xml;
using RoslynMCP.Languages;
using static RoslynMCP.Services.Packages.ConfigXml;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// The config file with its stale redirects retargeted, and nothing else about it changed.
/// </summary>
/// <remarks>
/// <para>
/// This used to be an <see cref="System.Xml.Linq.XDocument"/> round trip, which produced a correct
/// document and a diff nobody could read: the reader normalizes <c>\r\n</c> to <c>\n</c> in every
/// text node, so the whole file changed line endings, and the writer re-emits every empty element
/// in its own spelling, so <c>&lt;bindingRedirect …/&gt;</c> came back as
/// <c>&lt;bindingRedirect … /&gt;</c> on lines the fix had no business touching. Clicking "fix them
/// all" is supposed to move some version numbers, and a review of the result should show exactly
/// that.
/// </para>
/// <para>
/// So it edits the same full-fidelity tree the rest of the product reads XML with. Every character
/// of the source is a node, so a tree that was edited in two places and written back out is the
/// original file in every other place — there is no formatting pass to preserve anything through.
/// Indentation, line endings and where a new element's whitespace goes are
/// <see cref="XmlExtensions.AddElement"/>'s to work out from what the document already does, which
/// is why none of that is computed here.
/// </para>
/// <para>
/// The parse is error-tolerant, which changes what a broken file gets. A document whose redirects
/// are intact has its versions moved even if something further down is malformed — the edit is a
/// value between two quotes and cannot make that worse. What is refused is a document whose root
/// element was never closed, because a section added to one would be written wherever the parser
/// guessed the element ended.
/// </para>
/// </remarks>
internal static class BindingRedirectRewriter
{
    private const string AssemblyBindingXmlns = "urn:schemas-microsoft-com:asm.v1";

    private const string SectionPath = "runtime/assemblyBinding";

    /// <param name="applicable">Findings already filtered to the ones a rewrite can resolve.</param>
    /// <returns>The new text, or <c>null</c> when nothing needed to change.</returns>
    public static (string? Text, IReadOnlyList<BindingRedirectFinding> Applied) Rewrite(
        string xml, IReadOnlyList<BindingRedirectFinding> applicable)
    {
        var document = Parser.ParseText(xml);

        // A root the parser synthesized an end tag for is a file being typed into, not a file to
        // add a section to.
        if (document.RootSyntax is not XmlElementSyntax { EndTag.Span.Length: > 0 } configuration)
            return (null, []);

        var root = configuration;
        var applied = new List<BindingRedirectFinding>();

        foreach (var finding in applicable)
        {
            if (Retarget(root, finding) is not { } updated)
                continue;

            root = updated;
            applied.Add(finding);
        }

        if (applied.Count == 0)
            return (null, []);

        string text = document.ReplaceNode(configuration, root).ToFullString();

        // Every finding already named what ships. Handing back an identical document as a fix
        // would report a change the file does not have.
        return text == xml ? (null, []) : (text, applied);
    }

    /// <summary>
    /// One redirect pointed at the version that ships, added if it was not there.
    /// </summary>
    /// <remarks>
    /// <c>oldVersion</c> moves with <c>newVersion</c> and starts at zero rather than at the version
    /// that happened to be found: a redirect exists to catch every older binding, and narrowing it
    /// to the one this analysis saw is how a redirect that worked stops working after an unrelated
    /// package moves.
    /// </remarks>
    private static XmlElementSyntax? Retarget(XmlElementSyntax root, BindingRedirectFinding finding)
    {
        var section = Section(root);

        if (section is not null &&
            section.GetElementsByLocalName("dependentAssembly").FirstOrDefault(e => Matches(e, finding)) is { } existing)
        {
            return root.ReplaceNode(existing, WithRedirect(existing, finding, Prefix(section)));
        }

        // The innermost element the file already has, and everything below it as one detached
        // subtree. Growing the sections one at a time through the document would leave each of
        // them to be found again in the tree the last edit returned, for no gain: a section that
        // is missing has no content to preserve.
        var (host, added) = section is not null
            ? (section, Build(finding, Prefix(section)))
            : root.GetElementByLocalName("runtime") is { } runtime
                ? (runtime, Wrap("assemblyBinding", AssemblyBindingXmlns, Build(finding, "")))
                : ((XmlElementBaseSyntax)root,
                    Wrap("runtime", null, Wrap("assemblyBinding", AssemblyBindingXmlns, Build(finding, ""))));

        return root.ReplaceNode(host, host.AddChild(added.NormalizeTrivia(host)));
    }

    /// <summary>
    /// A whole <c>dependentAssembly</c>, built away from the document.
    /// </summary>
    /// <remarks>
    /// Written flat and handed to <see cref="XmlExtensions.NormalizeTrivia"/>, which indents it
    /// against the element it is going into — one level per step down, in whatever whitespace and
    /// line ending the document already uses.
    /// </remarks>
    private static XmlElementBaseSyntax Build(BindingRedirectFinding finding, string prefix)
    {
        string name = prefix.Length > 0 ? $"{prefix}:dependentAssembly" : "dependentAssembly";

        var element = (XmlElementBaseSyntax)Parser
            .ParseText($"<{name}></{name}>")
            .RootSyntax!;

        element = With(element, prefix, "assemblyIdentity", identity => identity
            .SetAttribute("name", finding.AssemblyName)
            .SetAttribute("publicKeyToken", finding.PublicKeyToken!)
            .SetAttribute("culture", finding.Culture));

        return With(element, prefix, "bindingRedirect", redirect => redirect
            .SetAttribute("oldVersion", $"0.0.0.0-{finding.RequiredVersion}")
            .SetAttribute("newVersion", finding.RequiredVersion));
    }

    /// <summary>One configured child element, named against the section's own prefix.</summary>
    private static XmlElementBaseSyntax With(
        XmlElementBaseSyntax parent,
        string prefix,
        string name,
        Func<XmlElementBaseSyntax, XmlElementBaseSyntax> configure) =>
        parent.AddElement(
            name,
            out _,
            (_, element) => configure(prefix.Length > 0 ? element.WithPrefixName(prefix) : element));

    /// <summary>An element wrapped around another, still flat and still detached.</summary>
    private static XmlElementBaseSyntax Wrap(string name, string? xmlns, XmlElementBaseSyntax child)
    {
        string attributes = xmlns is null ? "" : $" xmlns=\"{xmlns}\"";

        var element = (XmlElementBaseSyntax)Parser
            .ParseText($"<{name}{attributes}></{name}>")
            .RootSyntax!;

        return element.AddChild(child);
    }

    private static XmlElementBaseSyntax WithRedirect(
        XmlElementBaseSyntax dependentAssembly, BindingRedirectFinding finding, string prefix)
    {
        var parent = dependentAssembly;

        if (dependentAssembly.GetElementByLocalName("bindingRedirect") is not { } redirect)
        {
            parent = dependentAssembly.AddElement("bindingRedirect", out redirect);

            // A config that binds the assembly namespace to a prefix writes every element in the
            // section against it, and one written without would sit outside the section the
            // runtime reads — a redirect that is there and does nothing.
            if (prefix.Length > 0)
                redirect = redirect.WithPrefixName(prefix);
        }

        return parent.ReplaceNode(
            redirect,
            redirect
                .SetAttribute("oldVersion", $"0.0.0.0-{finding.RequiredVersion}")
                .SetAttribute("newVersion", finding.RequiredVersion));
    }

    private static string Prefix(XmlElementBaseSyntax element) =>
        element.NameNode?.Prefix ?? string.Empty;

    private static bool Matches(XmlElementBaseSyntax dependentAssembly, BindingRedirectFinding finding)
    {
        if (dependentAssembly.GetElementByLocalName("assemblyIdentity") is not { } identity)
            return false;

        return string.Equals(
                identity.GetAttributeValueByLocalName("name"),
                finding.AssemblyName,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                identity.GetAttributeValueByLocalName("publicKeyToken") ?? "",
                finding.PublicKeyToken ?? "",
                StringComparison.OrdinalIgnoreCase);
    }
}
