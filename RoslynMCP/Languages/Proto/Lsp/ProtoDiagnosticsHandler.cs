using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace RoslynMCP.Languages.Proto.Lsp;

/// <summary>
/// What is wrong with one <c>.proto</c>: what the parser could not read, the names that resolve to
/// nothing, the rules protoc enforces that the parser deliberately tolerates — and the one report
/// that is not a fault at all, that nothing has been generated from the file yet.
/// </summary>
/// <remarks>
/// <para>
/// Severity follows a single rule, and it is why two things protoc rejects outright are reported
/// here as warnings. A problem decidable from the file alone is an error: protoc will reject it and
/// nothing this pack guesses at can change that. A problem that depends on <em>finding</em> another
/// file is a warning, because <see cref="ProtoImportResolver"/> cannot see the per-item
/// <c>ProtoRoot</c> and <c>AdditionalImportDirs</c> metadata MSBuild hands protoc — a project that
/// sets either compiles cleanly while every import in it looks missing from here, and a wall of red
/// on a building solution is how a rule gets switched off for good.
/// </para>
/// <para>
/// The ids continue the parser's <c>PROTO0nn</c> sequence rather than starting a new one. They land
/// in the same problems list beside <see cref="ProtoDiagnosticIds"/> and Roslyn's own <c>CS…</c>
/// codes, and a user who suppresses one expects it to stay suppressed.
/// </para>
/// </remarks>
internal static class ProtoDiagnosticsHandler
{
    /// <summary>PROTO011 — "Import '{0}' was not found under any proto root for this file".</summary>
    private const string UnresolvedImport = "PROTO011";

    /// <summary>PROTO012 — "'{0}' names nothing this file declares or imports".</summary>
    private const string UnresolvedType = "PROTO012";

    /// <summary>PROTO013 — "Field number {0} is already used by '{1}'".</summary>
    private const string DuplicateFieldNumber = "PROTO013";

    /// <summary>PROTO014 — "'{0}' is already declared in this scope".</summary>
    private const string DuplicateName = "PROTO014";

    /// <summary>PROTO015 — "{0} is not a valid field number".</summary>
    private const string InvalidFieldNumber = "PROTO015";

    /// <summary>PROTO016 — "Field numbers 19000 to 19999 are reserved".</summary>
    private const string ReservedFieldNumber = "PROTO016";

    /// <summary>PROTO017 — "'required' was removed in proto3".</summary>
    private const string RequiredLabel = "PROTO017";

    /// <summary>PROTO018 — "The first value of a proto3 enum must be zero".</summary>
    private const string EnumFirstValue = "PROTO018";

    /// <summary>PROTO019 — "No generated C# was found for this file".</summary>
    private const string NotGenerated = "PROTO019";

    /// <summary>What the server calls itself in every diagnostic it publishes.</summary>
    private const string DiagnosticSource = "roslyn-sense";

    private const int MinFieldNumber = 1;

    /// <summary>2^29 - 1, the largest number a wire tag can carry.</summary>
    private const int MaxFieldNumber = 536870911;

    private const int FirstReservedFieldNumber = 19000;
    private const int LastReservedFieldNumber = 19999;

    public static async Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct)
    {
        if (await ProtoWorkspace.GetAsync(filePath, ct) is not { } view)
            return [];

        var file = view.Parse;
        var diagnostics = new List<Diagnostic>();

        foreach (var parsed in file.Diagnostics)
        {
            diagnostics.Add(At(
                file, parsed.Span, Severity(parsed.Severity), parsed.Id, parsed.Message));
        }

        // The type pass runs only when every import was found; see Imports.
        if (Imports(file, view.ProjectDirectory, diagnostics, ct))
            Types(view, diagnostics, ct);

        Declarations(file, diagnostics, ct);

        if (await NotGeneratedAsync(view, ct) is { } notGenerated)
            diagnostics.Add(notGenerated);

        return [.. diagnostics];
    }

    // ---- Imports ------------------------------------------------------------------------------

    /// <summary>
    /// Reports every <c>import</c> whose path names no file, and returns whether the file's imports
    /// resolved completely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The return value is what gates <see cref="Types"/>. A name that came from a file this could
    /// not read resolves to nothing for a reason that has nothing to do with the name, and lighting
    /// up every type in a file whose one import went missing buries the single problem that is real.
    /// </para>
    /// <para>
    /// A well-known import goes unreported but still counts against the gate. It does not have to
    /// exist on disk — <see cref="ProtoWellKnownTypes"/> answers for the types in it — but the table
    /// covers only the types it lists, and <c>google/protobuf/descriptor.proto</c> is not one of
    /// them.
    /// </para>
    /// </remarks>
    private static bool Imports(
        ProtoFile file, string? projectDirectory, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        bool complete = true;

        foreach (var import in file.Imports)
        {
            ct.ThrowIfCancellationRequested();

            if (ProtoImportResolver.Resolve(import.Path, file.FilePath, projectDirectory) is not null)
                continue;

            complete = false;

            if (ProtoWellKnownTypes.IsWellKnownPath(import.Path))
                continue;

            diagnostics.Add(At(
                file, import.PathSpan, DiagnosticSeverity.Warning, UnresolvedImport,
                $"Import '{import.Path}' was not found under any proto root for this file."));
        }

        return complete;
    }

    // ---- Type references ----------------------------------------------------------------------

    /// <summary>
    /// Every named type the file cannot see. The scope reproduces protobuf's visibility and its
    /// C++-style name lookup, so a name that fails here is a name protoc would refuse too.
    /// </summary>
    private static void Types(ProtoProjectView view, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var file = view.Parse;
        var scope = view.CreateScope();

        foreach (var reference in file.TypeReferences)
        {
            ct.ThrowIfCancellationRequested();

            // A scalar names a built-in rather than a declaration, so Resolve answers null for all
            // 15 of them by design. An empty name is what a recovered parse leaves behind, and the
            // parser has already said so.
            if (reference.IsScalar || reference.Text.Length == 0)
                continue;

            if (scope.Resolve(reference) is not null)
                continue;

            diagnostics.Add(At(
                file, reference.Span, DiagnosticSeverity.Warning, UnresolvedType,
                $"'{reference.Text}' names nothing this file declares or imports."));
        }
    }

    // ---- Declarations -------------------------------------------------------------------------

    private static void Declarations(ProtoFile file, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in file.AllDeclarations)
        {
            ct.ThrowIfCancellationRequested();

            // Keyed on the fully-qualified name, which is where protobuf's scoping already lives:
            // an enum value is named as if it were declared one level out, so two enums in one
            // message sharing a value name collide here exactly as protoc says they do. An extend
            // block is excluded because its full name is the name of the thing it extends, and a
            // file may legally extend the same message twice.
            if (declaration.Kind != ProtoDeclarationKind.Extend
                && declaration.Name.Value.Length > 0
                && !declared.Add(declaration.FullName))
            {
                diagnostics.Add(At(
                    file, declaration.Name.Span, DiagnosticSeverity.Error, DuplicateName,
                    $"'{declaration.Name.Value}' is already declared in this scope."));
            }

            switch (declaration)
            {
                case ProtoMessage message:
                    FieldNumbers(file, message, diagnostics);
                    break;

                case ProtoField field:
                    Field(file, field, diagnostics);
                    break;

                case ProtoEnum @enum when file.SyntaxLevel == ProtoSyntaxLevel.Proto3:
                    FirstEnumValue(file, @enum, diagnostics);
                    break;
            }
        }
    }

    /// <summary>
    /// A wire number two fields in one message both claim.
    /// </summary>
    /// <remarks>
    /// <see cref="ProtoMessage.AllFields"/> rather than <see cref="ProtoMessage.Fields"/>: a oneof's
    /// members are numbered in the enclosing message's space, so colliding with one is precisely the
    /// collision people write, and the narrower list would not see it.
    /// </remarks>
    private static void FieldNumbers(
        ProtoFile file, ProtoMessage message, List<Diagnostic> diagnostics)
    {
        var claimed = new Dictionary<int, ProtoField>();

        foreach (var field in message.AllFields)
        {
            // A field whose number the parser could not read carries a default 0 and no span to
            // point at, and has already been reported as a syntax error; letting it claim 0 would
            // invent a collision with whichever field really is numbered 0.
            if (field.NumberSpan.IsEmpty)
                continue;

            if (claimed.TryGetValue(field.Number, out var owner))
            {
                diagnostics.Add(At(
                    file, field.NumberSpan, DiagnosticSeverity.Error, DuplicateFieldNumber,
                    $"Field number {field.Number} is already used by '{owner.Name.Value}'."));
            }
            else
            {
                claimed[field.Number] = field;
            }
        }
    }

    /// <summary>
    /// One field's label and number.
    /// </summary>
    /// <remarks>
    /// The range checks apply to every dialect, not only to proto3: 0 has never been a field number
    /// and 19000-19999 has been protoc's own since proto2. Only the label rule is dialect-specific.
    /// </remarks>
    private static void Field(ProtoFile file, ProtoField field, List<Diagnostic> diagnostics)
    {
        if (field.Label == ProtoFieldLabel.Required && file.SyntaxLevel != ProtoSyntaxLevel.Proto2)
        {
            // The whole field declaration, because ProtoField carries no span for its label. The
            // squiggle covers more than the word at fault, which beats covering the wrong word.
            diagnostics.Add(At(
                file, field.Span, DiagnosticSeverity.Error, RequiredLabel,
                file.SyntaxLevel == ProtoSyntaxLevel.Proto3
                    ? "'required' was removed in proto3; a field is optional unless it is repeated."
                    : "'required' was removed after proto2; an edition file writes it as "
                      + "'[features.field_presence = LEGACY_REQUIRED]'."));
        }

        if (field.NumberSpan.IsEmpty)
            return;

        if (field.Number is < MinFieldNumber or > MaxFieldNumber)
        {
            diagnostics.Add(At(
                file, field.NumberSpan, DiagnosticSeverity.Error, InvalidFieldNumber,
                $"{field.Number} is not a valid field number; the range is "
                + $"{MinFieldNumber} to {MaxFieldNumber}."));
        }
        else if (field.Number is >= FirstReservedFieldNumber and <= LastReservedFieldNumber)
        {
            diagnostics.Add(At(
                file, field.NumberSpan, DiagnosticSeverity.Error, ReservedFieldNumber,
                $"Field numbers {FirstReservedFieldNumber} to {LastReservedFieldNumber} are "
                + "reserved for the protobuf implementation."));
        }
    }

    /// <summary>
    /// A proto3 enum whose first member is not zero, which is what protoc uses as the default.
    /// </summary>
    /// <remarks>
    /// proto3 only. An edition file settles it with <c>features.enum_type</c>, and a closed enum
    /// there may start anywhere — reporting one would be reporting the feature its author turned on.
    /// </remarks>
    private static void FirstEnumValue(ProtoFile file, ProtoEnum @enum, List<Diagnostic> diagnostics)
    {
        if (@enum.Values.IsDefaultOrEmpty)
            return;

        var first = @enum.Values[0];
        if (first.Number == 0)
            return;

        diagnostics.Add(At(
            file,
            first.NumberSpan.IsEmpty ? first.Name.Span : first.NumberSpan,
            DiagnosticSeverity.Error, EnumFirstValue,
            $"The first value of a proto3 enum must be zero; '{first.Name.Value}' is {first.Number}."));
    }

    // ---- The never-built case -----------------------------------------------------------------

    /// <summary>
    /// The report that says protoc has not run yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this file exists at all. With no generated C# every navigation feature in the pack
    /// answers "nothing", and "nothing" is indistinguishable from "nobody implements this service" —
    /// a user who has just checked the repository out would conclude the pack is broken, or worse,
    /// that their service has no implementation. Saying it once, in the problems list, is the
    /// difference between a pack that looks broken and one that says what it needs.
    /// </para>
    /// <para>
    /// Information, never a warning and never an error: the file is correct, the build simply has
    /// not run, and a contracts project nobody has built yet is an ordinary state for a checkout to
    /// be in. Anything louder would put a permanent mark on a clean tree.
    /// </para>
    /// <para>
    /// Anchored on the <c>syntax</c> statement rather than on the first <c>service</c>. The report
    /// is about the file — its messages bind no C# either, and a file with three services would have
    /// to pick one arbitrarily or say the same thing three times — and the syntax statement is the
    /// one construct that is about the file as a whole. When the file declares none, the first
    /// service is the next most useful place to look at, and the first line is the last resort.
    /// </para>
    /// <para>
    /// The gate is <see cref="ProtoGeneratedIndex.IsEmpty"/> for the whole project rather than
    /// <see cref="ProtoGeneratedIndex.DocumentsFor"/> for this file, which would also catch a
    /// <c>.proto</c> added since the last build. Per file is one binding away from a false positive:
    /// a file linked in from outside the project directory binds through a <c>source:</c> header
    /// resolved by path arithmetic, and a miss there would mark a file that is generated and
    /// building. A project with no generated documents at all cannot be a miss.
    /// </para>
    /// </remarks>
    private static async Task<Diagnostic?> NotGeneratedAsync(
        ProtoProjectView view, CancellationToken ct)
    {
        if (!view.Index.IsEmpty || view.Project is not { } project)
            return null;

        // Not every .proto under a project is compiled by it — a schema kept for reference, or one
        // the owning project simply has not listed yet. Naming a build that would generate nothing
        // for this file sends the user to fix the wrong thing.
        if (!await ProtoWorkspace.CompilesAsync(project, view.FilePath, ct))
            return null;

        return At(
            view.Parse, Anchor(view.Parse), DiagnosticSeverity.Info, NotGenerated,
            $"No generated C# was found for this file. Build '{project.Name}' to bind its "
            + "declarations to the classes protoc generates from it.");
    }

    private static TextSpan Anchor(ProtoFile file)
    {
        if (!file.SyntaxSpan.IsEmpty)
            return file.SyntaxSpan;

        if (!file.Services.IsDefaultOrEmpty)
            return file.Services[0].Name.Span;

        return file.Text.Lines[0].Span;
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static Diagnostic At(
        ProtoFile file, TextSpan span, DiagnosticSeverity severity, string id, string message) =>
        new(LspConverters.ToRange(file.Text.Lines, span),
            LspConverters.ToLspSeverity(severity),
            id,
            DiagnosticSource,
            message);

    private static DiagnosticSeverity Severity(ProtoDiagnosticSeverity severity) => severity switch
    {
        ProtoDiagnosticSeverity.Error => DiagnosticSeverity.Error,
        ProtoDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };
}
