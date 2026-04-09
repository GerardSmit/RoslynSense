using System.Text.RegularExpressions;

namespace RoslynMCP.Tools;

/// <summary>
/// Represents a file change produced by the rename operation.
/// </summary>
public sealed record RenameChangedFile(string FilePath, string OldText, string NewText);

/// <summary>
/// Shared text-replacement utilities used by both ASPX and Razor rename handlers.
/// </summary>
internal static class RenameHelper
{
    /// <summary>
    /// Replaces whole-word occurrences of oldName with newName on specific line numbers.
    /// </summary>
    public static string ReplaceOnLines(string text, HashSet<int> lineNumbers, string oldName, string newName)
    {
        var lines = text.Split('\n');
        bool changed = false;
        var pattern = $@"\b{Regex.Escape(oldName)}\b";

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            if (!lineNumbers.Contains(lineNum)) continue;

            string original = lines[i];
            string replaced = Regex.Replace(original, pattern, newName);
            if (replaced != original)
            {
                lines[i] = replaced;
                changed = true;
            }
        }

        return changed ? string.Join("\n", lines) : text;
    }

    /// <summary>
    /// Counts the number of occurrences of a search string in text.
    /// </summary>
    public static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
