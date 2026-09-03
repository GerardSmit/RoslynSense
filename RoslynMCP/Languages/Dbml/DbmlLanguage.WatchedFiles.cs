using RoslynMCP.Languages.Dbml.Core;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageWatchedFileHandler
{
    /// <summary>
    /// Drops what is cached for a model, or for the designer generated from one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves, because both can move without the editor saying so. A branch switch rewrites the
    /// <c>.dbml</c>; a regeneration — <c>regenerate_designer</c>, Visual Studio, or the refresh
    /// command in this pack — rewrites the <c>.designer.cs</c>. The parse cache only knows about the
    /// first, and the binding is invalidated by the project's semantic version moving, so the one
    /// thing left to clear by hand is the record of which designer files were proved to be SqlMetal's.
    /// A deleted model must stop claiming its designer, or F12 would go on withdrawing a location
    /// with nothing left to offer in its place.
    /// </para>
    /// <para>
    /// Returning false means "not mine", not "nothing to do".
    /// </para>
    /// </remarks>
    public bool Invalidate(string path, WatchedFileChange change)
    {
        if (DbmlDocumentCache.IsDbmlFile(path))
        {
            DbmlDocumentCache.Invalidate(path);

            if (change is WatchedFileChange.Deleted)
                DbmlSourceMappingService.Forget(DbmlSourceMappingService.DesignerPathFor(path));

            return true;
        }

        if (DbmlSourceMappingService.ModelPathFor(path) is null)
            return false;

        // A designer that was rewritten or removed is one whose binding has to be re-derived. Only a
        // path this pack recognises as a designer reaches here, and only a bound one was ever
        // recorded, so a `.resx`'s designer answers false and is left to whoever owns it.
        return DbmlSourceMappingService.Forget(path);
    }
}
