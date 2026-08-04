using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>One <c>&lt;add&gt;</c> under <c>appSettings</c> or <c>connectionStrings</c>.</summary>
/// <param name="Provider">The <c>providerName</c> of a connection string; null for an app
/// setting.</param>
/// <param name="NameSpan">The naming attribute's value, quotes excluded, or
/// <see langword="default"/> when it could not be located in the text — an entity in the name puts
/// the decoded value and the file out of step, and a range that is merely close is worse than
/// none.</param>
internal readonly record struct WebConfigSetting(
    string Name, string? Value, string? Provider, string FilePath, TextSpan NameSpan);

/// <summary>
/// The <c>&lt;appSettings&gt;</c> and <c>&lt;connectionStrings&gt;</c> a markup file can see, for
/// the two expression builders that read configuration rather than resources.
/// </summary>
/// <remarks>
/// A deliberately shallow reading of what the runtime does: the <c>web.config</c> files from the
/// project directory down to the file's own, nearer overriding further. Machine-level
/// configuration, <c>configSource</c> redirection and build-time transforms are all out — this
/// exists so that <c>&lt;%$ AppSettings: CdnRoot %&gt;</c> hovers to a value instead of to nothing,
/// and a prefix that resolves to nothing here answers nothing rather than reporting a problem.
/// <para>
/// Cached against each file's timestamp and nothing else, the way the tag-prefix read in
/// <see cref="AspxDocumentService"/> is. A <c>web.config</c> is not a document the editor opens,
/// so there is no buffer to prefer and no invalidation to wire — the next read after a write sees
/// a different timestamp and re-parses.
/// </para>
/// </remarks>
internal static class WebConfigSettings
{
    private sealed record FileEntry(
        DateTime WriteTimeUtc,
        ImmutableArray<WebConfigSetting> AppSettings,
        ImmutableArray<WebConfigSetting> ConnectionStrings);

    private static readonly FileEntry s_empty = new(default, [], []);

    private static readonly ConcurrentDictionary<string, FileEntry> s_files =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImmutableArray<WebConfigSetting> AppSettings(AspxDocument document) =>
        Merged(document, connectionStrings: false);

    public static ImmutableArray<WebConfigSetting> ConnectionStrings(AspxDocument document) =>
        Merged(document, connectionStrings: true);

    /// <summary>
    /// The entry a <c>&lt;%$ ConnectionStrings: … %&gt;</c> argument names. The builder accepts a
    /// bare name and a <c>.ProviderName</c> suffix, and the suffixed form asks for a different
    /// field of the same entry rather than for a different entry.
    /// </summary>
    public static (WebConfigSetting Setting, bool Provider)? ConnectionString(
        AspxDocument document, string argument)
    {
        const string providerSuffix = ".ProviderName";

        var settings = ConnectionStrings(document);

        if (Find(settings, argument) is { } direct)
            return (direct, false);

        return argument.EndsWith(providerSuffix, StringComparison.OrdinalIgnoreCase)
            && Find(settings, argument[..^providerSuffix.Length]) is { } named
                ? (named, true)
                : null;
    }

    public static WebConfigSetting? Find(ImmutableArray<WebConfigSetting> settings, string name)
    {
        foreach (var setting in settings)
        {
            if (setting.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return setting;
        }

        return null;
    }

    private static ImmutableArray<WebConfigSetting> Merged(AspxDocument document, bool connectionStrings)
    {
        var byName = new Dictionary<string, WebConfigSetting>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (string path in Chain(document))
        {
            var file = Read(path);

            foreach (var setting in connectionStrings ? file.ConnectionStrings : file.AppSettings)
            {
                if (!byName.ContainsKey(setting.Name))
                    order.Add(setting.Name);

                byName[setting.Name] = setting;
            }
        }

        var merged = ImmutableArray.CreateBuilder<WebConfigSetting>(order.Count);

        foreach (string name in order)
            merged.Add(byName[name]);

        return merged.ToImmutable();
    }

    /// <summary>The <c>web.config</c> files above the markup file, application root first so that
    /// the nearest one is applied last and wins.</summary>
    private static IEnumerable<string> Chain(AspxDocument document)
    {
        if (Path.GetDirectoryName(document.Project.FilePath) is not { Length: > 0 } root)
            yield break;

        var directories = new List<string>();

        for (string? directory = Path.GetDirectoryName(document.FilePath);
             directory is { Length: > 0 };
             directory = Path.GetDirectoryName(directory))
        {
            directories.Add(directory);

            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
                break;
        }

        // A markup file outside the project directory still gets the application's own settings.
        if (directories.Count == 0
            || !directories[^1].Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            directories.Add(root);
        }

        for (int i = directories.Count - 1; i >= 0; i--)
        {
            if (Locate(directories[i]) is { } path)
                yield return path;
        }
    }

    /// <summary>Both spellings, because the file is <c>Web.config</c> in a Visual Studio project
    /// and <c>web.config</c> in most of the world, and only one of them exists.</summary>
    private static string? Locate(string directory)
    {
        foreach (string name in new[] { "web.config", "Web.config" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static FileEntry Read(string path)
    {
        DateTime writeTime;

        try
        {
            var info = new FileInfo(path);
            writeTime = info.Exists ? info.LastWriteTimeUtc : default;
        }
        catch (IOException)
        {
            return s_empty;
        }

        if (s_files.TryGetValue(path, out var cached) && cached.WriteTimeUtc == writeTime)
            return cached;

        var entry = Parse(path, writeTime);
        s_files[path] = entry;
        return entry;
    }

    /// <summary>
    /// The document is read twice on purpose: <see cref="XDocument"/> for the values, because it
    /// decodes entities and sees through <c>&lt;location&gt;</c> wrappers, and the raw text for the
    /// spans, because decoding is exactly what makes an <see cref="XDocument"/> position wrong.
    /// </summary>
    private static FileEntry Parse(string path, DateTime writeTime)
    {
        string? text = ReadText(path);
        if (text is null)
            return s_empty;

        XDocument document;

        try
        {
            document = XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return s_empty;
        }

        var appSettings = ImmutableArray.CreateBuilder<WebConfigSetting>();
        var connectionStrings = ImmutableArray.CreateBuilder<WebConfigSetting>();

        foreach (var add in document.Descendants("appSettings").Elements("add"))
        {
            if (add.Attribute("key")?.Value is not { Length: > 0 } key)
                continue;

            appSettings.Add(new WebConfigSetting(
                key, add.Attribute("value")?.Value, null, path, ValueSpan(text, "key", key)));
        }

        foreach (var add in document.Descendants("connectionStrings").Elements("add"))
        {
            if (add.Attribute("name")?.Value is not { Length: > 0 } name)
                continue;

            connectionStrings.Add(new WebConfigSetting(
                name,
                add.Attribute("connectionString")?.Value,
                add.Attribute("providerName")?.Value,
                path,
                ValueSpan(text, "name", name)));
        }

        return new FileEntry(writeTime, appSettings.ToImmutable(), connectionStrings.ToImmutable());
    }

    /// <summary>The span of <c>attribute="value"</c>'s value in the raw text, or
    /// <see langword="default"/> when the pair does not occur verbatim.</summary>
    private static TextSpan ValueSpan(string text, string attribute, string value)
    {
        foreach (char quote in "\"'")
        {
            string needle = $"{attribute}={quote}{value}{quote}";

            for (int i = text.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                // `key="x"` must not be found inside `mykey="x"`.
                if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
                    continue;

                return new TextSpan(i + attribute.Length + 2, value.Length);
            }
        }

        return default;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
