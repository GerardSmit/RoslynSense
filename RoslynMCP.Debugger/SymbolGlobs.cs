using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Debugger;

/// <summary>
/// The symbol load policy: which modules get their PDBs opened, decided by the include and
/// exclude globs in <see cref="DebugDisplayOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The same dial Visual Studio's symbol settings expose as "Load only specified modules" /
/// "Load all modules, unless excluded". Exclude wins over include, and an empty include list
/// means "everything not excluded" — so the default (both empty) loads symbols for every
/// module, which is what a session with no configuration must do.
/// </para>
/// <para>
/// A glob without a path separator matches the module's file name, so <c>App_Web_*.dll</c>
/// reads the way it would in VS; one with a separator matches the full path, which is how a
/// whole directory tree like <c>**\Temporary ASP.NET Files\**</c> is named. Matching is
/// case-insensitive because Windows paths are.
/// </para>
/// </remarks>
public static class SymbolGlobs
{
    /// <summary>Compiled per pattern, not per module load — a WebForms site asks about the
    /// same handful of globs hundreds of times.</summary>
    private static readonly ConcurrentDictionary<string, Regex> s_compiled = new();

    /// <summary>Whether <paramref name="modulePath"/>'s symbols should load under
    /// <paramref name="options"/>.</summary>
    public static bool WantsSymbols(DebugDisplayOptions options, string modulePath)
    {
        if (modulePath.Length == 0)
            return true;

        foreach (var glob in options.SymbolExclude)
        {
            if (Matches(glob, modulePath))
                return false;
        }

        if (options.SymbolInclude.Length == 0)
            return true;

        foreach (var glob in options.SymbolInclude)
        {
            if (Matches(glob, modulePath))
                return true;
        }
        return false;
    }

    internal static bool Matches(string glob, string modulePath)
    {
        if (glob.Length == 0)
            return false;
        var subject = glob.AsSpan().IndexOfAny('\\', '/') < 0
            ? Path.GetFileName(modulePath)
            : modulePath;
        return s_compiled.GetOrAdd(glob, Compile).IsMatch(subject);
    }

    /// <c>**</c> crosses directories, <c>*</c> and <c>?</c> stop at a separator, and either
    /// slash matches either separator — a glob written on one OS keeps working on the other.
    private static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    pattern.Append(".*");
                    i++;
                    break;
                case '*':
                    pattern.Append(@"[^\\/]*");
                    break;
                case '?':
                    pattern.Append(@"[^\\/]");
                    break;
                case '\\' or '/':
                    pattern.Append(@"[\\/]");
                    break;
                case var c:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
