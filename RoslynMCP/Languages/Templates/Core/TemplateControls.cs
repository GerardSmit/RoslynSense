using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>
/// The control that renders a module the template files never declared, found by where it lives.
/// </summary>
/// <remarks>
/// <para>
/// A quarter of the modules a template folder hosts are not declared in it. They were installed
/// into the application rather than described by it — the registration is in a database or in a
/// package manifest that was applied once and thrown away — so the files name them and say nothing
/// about them, and a row for one of those screens would have a Definition and no Implementation.
/// That is the row a reader most needs the second half of: the screen exists, something renders it,
/// and the files do not say what.
/// </para>
/// <para>
/// What they do have is a convention, and it is the same one that makes the module name mean
/// anything: a module lives in a folder named after itself, holding a control named after itself.
/// So the name is looked up as a folder, and the view control inside it is the answer. Guessing,
/// but a guess that is checked against the disk before it is offered — the button appears only
/// when the file it would open is really there.
/// </para>
/// <para>
/// Held for as long as the templates it belongs to are, which is until one of them is edited. A
/// control folder appearing without any template file changing is a checkout that has just gained
/// a module nothing hosts yet, and the row for it does not exist either.
/// </para>
/// </remarks>
internal sealed class TemplateControls
{
    /// <summary>What a module's own control file is called, after the module's name.</summary>
    /// <remarks>
    /// The view suffix first, then the bare name. Every other control in the folder is a mode —
    /// <c>_Edit</c>, <c>_Settings</c> — and opening the settings screen when the row named the
    /// page is the wrong half of the answer, so nothing else is accepted.
    /// </remarks>
    private static readonly string[] Suffixes = ["_View.ascx", ".ascx"];

    /// <summary>What any view control in a module's folder is called, whatever the module is.</summary>
    private const string AnyView = "*_View.ascx";

    private readonly string _contentRoot;
    private readonly ImmutableArray<string> _folders;
    private readonly ConcurrentDictionary<string, string?> _found =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<ImmutableArray<string>> _directories;

    public TemplateControls(string contentRoot, ImmutableArray<string> folders)
    {
        _contentRoot = contentRoot;
        _folders = folders;
        _directories = new Lazy<ImmutableArray<string>>(Directories);
    }

    /// <summary>The control file for a module of this name, or null when there is none.</summary>
    public string? Find(string moduleType)
    {
        if (_contentRoot.Length == 0 || _folders.IsDefaultOrEmpty || moduleType.Length == 0)
            return null;

        // A module named by the package it ships in is looked up by its own name: the folder is
        // named after the module, and the package is the folder above it.
        string name = moduleType[(moduleType.LastIndexOf('.') + 1)..];

        return name.Length == 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains('/')
            ? null
            : _found.GetOrAdd(name, Search);
    }

    private string? Search(string name)
    {
        foreach (string directory in _directories.Value)
        {
            string folder = Path.Combine(directory, name);

            if (!Directory.Exists(folder))
                continue;

            foreach (string suffix in Suffixes)
            {
                string candidate = Path.Combine(folder, name + suffix);

                if (File.Exists(candidate))
                    return candidate;
            }

            if (OnlyView(folder) is { Length: > 0 } only)
                return only;
        }

        return null;
    }

    /// <summary>
    /// The one view control in a folder, when there is exactly one.
    /// </summary>
    /// <remarks>
    /// A folder is not always named quite what its control is — a module registered as
    /// <c>Orders_CMS</c> living in <c>Orders_CMS</c> and serving <c>Orders_View.ascx</c> is
    /// ordinary, because the folder was named after the registration and the file after the thing.
    /// One candidate is not a guess; two is, and two gets nothing, because opening the wrong screen
    /// is worse than opening none.
    /// </remarks>
    private static string? OnlyView(string folder)
    {
        try
        {
            string[] views = Directory.GetFiles(folder, AnyView, SearchOption.TopDirectoryOnly);

            return views.Length == 1 ? views[0] : null;
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

    /// <summary>
    /// Where a module folder could be: the configured folders, and the packages inside them.
    /// </summary>
    /// <remarks>
    /// Two levels rather than a recursive walk. An application's modules sit one package deep, and
    /// a walk of the whole application root is thousands of directories of content to answer a
    /// question about a name.
    /// </remarks>
    private ImmutableArray<string> Directories()
    {
        var directories = ImmutableArray.CreateBuilder<string>();

        foreach (string folder in _folders)
        {
            string root = Path.Combine(
                _contentRoot, folder.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(root))
                continue;

            directories.Add(root);

            try
            {
                directories.AddRange(Directory.EnumerateDirectories(root));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return directories.ToImmutable();
    }
}
