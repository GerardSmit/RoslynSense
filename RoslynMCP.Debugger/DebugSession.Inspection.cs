using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// <summary>
/// The half of the engine that decides how a value is <em>presented</em>: the display string a
/// type asks for, the children an expansion shows, and which frames a step is allowed to land in.
/// </summary>
/// <remarks>
/// All of it is driven by the <c>System.Diagnostics</c> attributes in the debuggee's own
/// metadata — <c>DebuggerDisplay</c>, <c>DebuggerTypeProxy</c>, <c>DebuggerBrowsable</c>,
/// <c>DebuggerStepThrough</c> and friends — and all of it is switchable through
/// <see cref="DisplayOptions"/>, because every one of these attributes exists to hide something,
/// and a debugger that cannot be told to stop hiding is useless on the day the attribute is what
/// is wrong.
/// </remarks>
public sealed partial class DebugSession
{
    /// <summary>Path segment that constructs the value's <c>DebuggerTypeProxy</c>.</summary>
    internal const string ProxyMarker = "$proxy";

    /// <summary>Path segment that suppresses proxy and browsable filtering for one expansion —
    /// the "Raw View" node.</summary>
    internal const string RawMarker = "$raw";

    /// <summary>Path segment that materializes a lazy enumerable's elements — the "Results View"
    /// node VS offers on an IEnumerable.</summary>
    internal const string ResultsMarker = "$results";

    /// <summary>Pseudo-variable naming the exception the session is stopped on.</summary>
    internal const string ExceptionMarker = "$exception";

    /// <summary>Path segment that expands a type's static state — the "Static members" node.</summary>
    internal const string StaticsMarker = "$statics";

    /// <summary>
    /// Path segment prefix naming a span of elements: <c>$more:N</c> from element N to the end,
    /// or <c>$more:N:M</c> for the half-open range <c>[N, M)</c>.
    /// </summary>
    /// <remarks>
    /// The closed form is what makes a long array navigable. Listing a hundred thousand elements
    /// a page at a time means a page press per page; listing them as a tree of ranges reaches any
    /// element in a handful of expansions.
    /// </remarks>
    internal const string MoreMarker = "$more";

    private DebugDisplayOptions _display = new();

    /// <summary>
    /// Which modules hold the user's code, rebuilt whenever the options carrying the solution's
    /// assemblies are replaced.
    /// </summary>
    private volatile UserCodeMap _userCode = UserCodeMap.None;

    /// <summary>
    /// Which debugger attributes this session honours. Replaceable at any time, including while
    /// stopped, so a display string that looks wrong can be switched off and the raw fields read
    /// without restarting the target.
    /// </summary>
    public DebugDisplayOptions DisplayOptions
    {
        get => _display;
        set
        {
            _display = value ?? new DebugDisplayOptions();
            _userCode = UserCodeMap.From(_display.UserAssemblies);
            // Already-loaded modules were marked against the previous answer, and the runtime keeps
            // that marking until it is told otherwise.
            RemarkLoadedModules();
        }
    }

    /// <summary>Whether this module could hold code the user wrote.</summary>
    /// <remarks>Instance rather than static because the answer depends on which solution is open.
    /// Fail-open: a module nothing could classify counts as the user's.</remarks>
    internal bool IsUserModule(string moduleName) => _userCode.CouldBeUserCode(moduleName);

    /// <summary>
    /// Guards against a <c>DebuggerDisplay</c> whose expressions describe values that are
    /// themselves displayed by an attribute. Legal and common (a node showing its parent), and
    /// unbounded without a limit.
    /// </summary>
    private int _displayDepth;

    private const int MaxDisplayDepth = 3;

    /// <summary>How far up the inheritance chain to look for an attribute or a member.</summary>
    private const int MaxTypeDepth = 16;

    // --- expansion ------------------------------------------------------------------------------

    /// <summary>
    /// The children of the value at <paramref name="path"/> — fields, array elements, or whatever
    /// a <c>DebuggerTypeProxy</c> substitutes for them.
    /// </summary>
    /// <remarks>
    /// Children are addressed by path rather than by handle so that the answer survives the
    /// process boundary to a bitness-matched worker, and so that any child can be re-read,
    /// evaluated, or assigned through the same expression grammar the user types.
    /// </remarks>
    public Task<List<DebugVariable>> ExpandAsync(uint frameIndex, string path) => InvokeAsync(() =>
    {
        var thread = _stoppedThread;
        if (thread is null)
            return new List<DebugVariable>();
        if (FrameAt(thread, frameIndex) is not CorDebugILFrame ilFrame)
            return new List<DebugVariable>();

        if (string.IsNullOrWhiteSpace(path))
            return FrameVariables(ilFrame);

        var value = ResolvePath(ilFrame, path, out _);
        return value is null ? new List<DebugVariable>() : ChildrenOf(value, path);
    });

    private List<DebugVariable> ChildrenOf(CorDebugValue value, string path)
    {
        var children = new List<DebugVariable>();

        // An index-range row names the same value with the span it stands for. The open form
        // ($more:N) continues to the end; the closed form ($more:N:M) is one node of the range
        // tree a long array is listed through.
        var offset = 0;
        var limit = int.MaxValue;
        var basePath = path;
        var moreAt = path.LastIndexOf($".{MoreMarker}:", StringComparison.Ordinal);
        if (moreAt >= 0)
        {
            var span = path[(moreAt + MoreMarker.Length + 2)..].Split(':');
            if (int.TryParse(span[0], out var parsed))
            {
                offset = parsed;
                basePath = path[..moreAt];
                if (span.Length > 1 && int.TryParse(span[1], out var parsedEnd))
                    limit = parsedEnd;
            }
        }

        var target = Safe(() => Dereference(value));
        if (target is null || target is CorDebugStringValue)
            return children;

        if (target is CorDebugArrayValue array)
        {
            AppendElements(children, array, basePath, offset, limit);
            return children;
        }

        if (target is not CorDebugObjectValue)
            return children;

        if (basePath.EndsWith("." + StaticsMarker, StringComparison.Ordinal))
        {
            AppendStatics(children, value, basePath);
            return children;
        }

        // A "Raw View" expansion asks for exactly what is in memory, so neither the proxy nor the
        // browsable states apply to it.
        var raw = basePath.EndsWith("." + RawMarker, StringComparison.Ordinal);

        if (!raw && _display.TypeProxy && ProxyTypeNameOf(value) is not null)
        {
            AppendProxyMembers(children, value, basePath);
            if (_display.RawView)
                children.Add(RawViewRow(basePath));
            return children;
        }

        var hidNothing = AppendFields(
            children, value, basePath,
            applyBrowsable: !raw && _display.Browsable,
            includeProperties: !raw);

        if (!raw && HasStaticMembers(value))
            children.Add(new DebugVariable
            {
                Name = "Static members",
                Value = "the type's static state",
                Kind = "statics",
                Type = string.Empty,
                VariablesReference = $"{basePath}.{StaticsMarker}",
            });

        // A lazy enumerable — a LINQ query, an iterator — shows its internals above; the elements
        // it would produce are behind a Results View, which enumerates only when asked to.
        if (!raw && _display.TypeProxy && ImplementsEnumerable(value))
            children.Add(new DebugVariable
            {
                Name = "Results View",
                Value = $"expanding this runs the sequence, up to {MaxMaterializedElements} elements",
                Kind = "results",
                Type = string.Empty,
                VariablesReference = $"{basePath}.{ResultsMarker}",
            });

        if (!raw && !hidNothing && _display.RawView)
            children.Add(RawViewRow(basePath));

        return children;
    }

    /// <summary>Whether the value's type declares any static field — the gate for offering a
    /// "Static members" node. Constants count: they are static state as VS shows it.</summary>
    private static bool HasStaticMembers(CorDebugValue value)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            // Framework roots would put a "Static members" node on every last object; like VS,
            // the node describes the user's part of the chain.
            var typeName = Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef);
            if (typeName is null || typeName.StartsWith("System.", StringComparison.Ordinal))
                return false;

            foreach (var field in Fields(metadata, typeDef))
            {
                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is not null &&
                    (props.Value.pdwAttr.HasFlag(CorFieldAttr.fdStatic) ||
                     props.Value.pdwAttr.HasFlag(CorFieldAttr.fdLiteral)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lists a type's static state: static fields read through the runtime, constants decoded
    /// from metadata, and static property getters evaluated like any other computed member.
    /// </summary>
    private void AppendStatics(List<DebugVariable> into, CorDebugValue value, string path)
    {
        var frame = _inspectionFrame;
        var budget = Math.Max(1, _display.MaxChildren);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            var typeName = Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef);
            if (typeName is null || typeName.StartsWith("System.", StringComparison.Ordinal))
                return;

            foreach (var field in Fields(metadata, typeDef))
            {
                if (into.Count >= budget)
                    return;

                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is null)
                    continue;
                var attributes = props.Value.pdwAttr;
                var name = props.Value.szField;
                if (string.IsNullOrEmpty(name) || name.StartsWith("<", StringComparison.Ordinal) ||
                    !emitted.Add(name))
                    continue;

                if (attributes.HasFlag(CorFieldAttr.fdLiteral))
                {
                    // A constant has no storage to read; its value is in the metadata itself.
                    if (ConstantDisplayOf(props.Value) is { } constant)
                        into.Add(new DebugVariable { Name = name, Value = constant, Kind = "constant" });
                    continue;
                }
                if (!attributes.HasFlag(CorFieldAttr.fdStatic))
                    continue;

                var member = Safe(() => cls.GetStaticFieldValue((int)field, frame?.Raw));
                if (member is not null)
                    into.Add(Row(name, member, "static", $"{path}.{name}"));
            }

            foreach (var property in Properties(metadata, typeDef))
            {
                if (into.Count >= budget)
                    return;

                var props = Safe<GetPropertyPropsResult?>(() => metadata.GetPropertyProps(property));
                if (props is null || props.Value.pmdGetter.Rid == 0)
                    continue;
                var name = props.Value.szProperty;
                if (string.IsNullOrEmpty(name) || name == "Item" || !emitted.Add(name))
                    continue;
                if (Safe(() => metadata.GetMethodProps(props.Value.pmdGetter).pdwAttr.HasFlag(CorMethodAttr.mdStatic)) != true)
                    continue;

                var getter = Safe(() => cls.Module.GetFunctionFromToken(props.Value.pmdGetter));
                if (getter is null)
                    continue;
                var member = InvokeFunction(getter, [], out _);
                if (member is not null)
                    into.Add(Row(name, member, "static", $"{path}.{name}"));
            }
        }
    }

    /// <summary>A constant's metadata value, rendered the way the equivalent runtime value would
    /// be.</summary>
    private static string? ConstantDisplayOf(GetFieldPropsResult props)
    {
        if (props.pdwCPlusTypeFlag == CorElementType.String)
            return props.ppValue == IntPtr.Zero
                ? null
                : QuoteString(Marshal.PtrToStringUni(props.ppValue, props.pcchValue) ?? string.Empty, truncated: false);
        return ConstantOf(props)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the value's type declares <c>System.Collections.IEnumerable</c> anywhere
    /// in its chain — the gate for offering a Results View.</summary>
    private static bool ImplementsEnumerable(CorDebugValue value)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            foreach (var impl in InterfaceImpls(metadata, typeDef))
            {
                var name = Safe(() =>
                {
                    var itf = metadata.GetInterfaceImplProps(impl).ptkIface;
                    return itf.Type switch
                    {
                        CorTokenType.mdtTypeRef => metadata.GetTypeRefProps((mdTypeRef)itf).szName,
                        CorTokenType.mdtTypeDef => metadata.GetTypeDefProps((mdTypeDef)itf).szTypeDef,
                        _ => null,
                    };
                });
                if (name is "System.Collections.IEnumerable")
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<mdInterfaceImpl> InterfaceImpls(MetaDataImport metadata, mdTypeDef typeDef)
    {
        var handle = IntPtr.Zero;
        var buffer = new mdInterfaceImpl[16];
        try
        {
            while (true)
            {
                var read = 0;
                try { read = metadata.EnumInterfaceImpls(ref handle, typeDef, buffer); }
                catch { read = 0; }
                if (read <= 0)
                    yield break;
                for (var i = 0; i < read; i++)
                    yield return buffer[i];
                if (read < buffer.Length)
                    yield break;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
                Try(() => metadata.CloseEnum(handle));
        }
    }

    /// <summary>
    /// Materializes a lazy enumerable's elements in the debuggee, through System.Core's own
    /// enumerable debug view — the same type VS's Results View constructs.
    /// </summary>
    /// <remarks>Running the enumeration is the point and the price: a query with side effects
    /// runs them, exactly as it does under VS. The view's <c>Items</c> getter throws on an empty
    /// sequence, which surfaces here as an error rather than a list.</remarks>
    private CorDebugValue? EnumerableItems(CorDebugValue value, out string error)
    {
        error = string.Empty;

        var viewType = EnumerableViewNames
            .Select(name => FindTypeDef(value, name))
            .FirstOrDefault(found => found is not null);
        if (viewType is null)
        {
            error = "no enumerable debug view type is loaded in the target, so the elements " +
                    "cannot be materialized without running the query by hand";
            return null;
        }

        var (module, typeDef) = viewType.Value;
        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
        var ctorToken = metadata is null ? null : Safe(() =>
        {
            var found = metadata.FindMethod(typeDef, ".ctor", IntPtr.Zero, 0);
            return (mdMethodDef?)found;
        });
        var ctor = ctorToken is { } token ? Safe(() => module.GetFunctionFromToken(token)) : null;
        if (ctor is null)
        {
            error = "the enumerable view's constructor could not be resolved";
            return null;
        }

        // Bound the sequence before materializing it where the element type is known. Without
        // this, expanding an infinite iterator enumerates until the debuggee runs out of memory —
        // and the user asked to look at a value, not to run their program to destruction.
        var source = BoundedSequence(value) ?? value;

        var view = RunEval(eval => eval.NewObject(ctor.Raw, 1, [source.Raw]), out error);
        return view is null ? null : MemberValue(view, "Items", callOnly: false, out error);
    }

    /// <summary>
    /// The debug view types that materialize an <c>IEnumerable</c>, in the names the various
    /// runtimes give them.
    /// </summary>
    /// <remarks>
    /// The original name is a .NET Framework one, declared in System.Core. Looking only for that
    /// meant the Results View was silently missing on every modern runtime — the node offered
    /// itself and then reported that System.Core was not loaded, which on .NET it never is.
    /// </remarks>
    private static readonly string[] EnumerableViewNames =
    [
        "System.Linq.SystemCore_EnumerableDebugView",
        "System.Linq.EnumerableDebugView",
        "System.Collections.Generic.CollectionDebugView",
    ];

    /// <summary>
    /// How many elements a Results View materializes at most.
    /// </summary>
    /// <remarks>
    /// Larger than a page so the listing has something to page through, and small enough that
    /// expanding the node is never itself the expensive operation.
    /// </remarks>
    private const int MaxMaterializedElements = 1000;

    /// <summary>
    /// <c>Enumerable.Take(source, n)</c> over the value, or null when the element type cannot be
    /// determined and the sequence has to be taken whole.
    /// </summary>
    /// <remarks>
    /// The element type comes from the runtime's own instantiation rather than from decoding a
    /// metadata signature, and only the unambiguous case is used: exactly one type argument, which
    /// is what <c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c> and a plain <c>IEnumerable&lt;T&gt;</c>
    /// all are. A multi-parameter iterator would need a convention about which parameter is the
    /// element, and guessing wrong there produces a <c>TypeLoadException</c> inside the evaluation
    /// rather than a wrong answer — so those fall back to the unbounded path instead.
    /// </remarks>
    private CorDebugValue? BoundedSequence(CorDebugValue value)
    {
        var typeArguments = TypeArgumentsOf(value);
        if (typeArguments is not { Length: 1 })
            return null;

        var enumerable = FindTypeDef(value, "System.Linq.Enumerable");
        if (enumerable is not { } found)
            return null;

        var (module, typeDef) = found;
        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
        if (metadata is null)
            return null;

        var takeToken = FindTakeCount(metadata, typeDef);
        var take = takeToken is { } token ? Safe(() => module.GetFunctionFromToken(token)) : null;
        if (take is null)
            return null;

        var ignored = string.Empty;
        var limit = CreateIntValue(MaxMaterializedElements, ref ignored);
        if (limit is null)
            return null;

        return RunEval(
            eval => eval.CallParameterizedFunction(
                take.Raw, typeArguments.Length, typeArguments, 2, [value.Raw, limit.Raw]),
            out _);
    }

    /// <summary>
    /// The token of <c>Enumerable.Take&lt;T&gt;(IEnumerable&lt;T&gt;, int)</c> specifically.
    /// </summary>
    /// <remarks>
    /// Asking for "Take" by name alone returns whichever overload the metadata enumerates first,
    /// and since .NET 6 there are two: the count one and <c>Take(source, Range)</c>. Picking the
    /// Range overload makes the evaluation fault, the error is discarded, and the caller falls back
    /// to materializing the sequence whole — so the guard against an infinite iterator would
    /// silently stop existing, on some runtimes and not others. The last byte of the signature is
    /// the parameter type, which is what tells them apart.
    /// </remarks>
    private static mdMethodDef? FindTakeCount(MetaDataImport metadata, mdTypeDef typeDef)
    {
        const byte ElementTypeI4 = 0x08;

        var handle = IntPtr.Zero;
        try
        {
            var candidates = new mdMethodDef[16];
            int found = Safe(() => (int?)metadata.EnumMethodsWithName(
                ref handle, typeDef, "Take", candidates)) ?? 0;

            for (int i = 0; i < found; i++)
            {
                var props = Safe(() => metadata.GetMethodProps(candidates[i]));
                if (props is not { pcbSigBlob: > 0 } signature || signature.ppvSigBlob == IntPtr.Zero)
                    continue;

                if (Marshal.ReadByte(signature.ppvSigBlob, signature.pcbSigBlob - 1) == ElementTypeI4)
                    return candidates[i];
            }
        }
        catch
        {
            // No usable overload; the caller materializes the sequence whole instead.
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { metadata.CloseEnum(handle); } catch { }
            }
        }

        return null;
    }

    private DebugVariable RawViewRow(string path) => new()
    {
        Name = "Raw View",
        Value = "the object's own fields, unfiltered",
        Kind = "raw",
        Type = string.Empty,
        VariablesReference = $"{path}.{RawMarker}",
    };

    /// <summary>
    /// Lists an array's elements from <paramref name="from"/>, one page at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Past a threshold the listing becomes a tree of index ranges instead of a flat page with a
    /// "..." row. A flat listing is fine for a hundred elements and unusable for a hundred
    /// thousand: continuing the page is the only way forward, so reaching element 90,000 means
    /// nine hundred expansions. Ranges get there in three.
    /// </para>
    /// <para>
    /// The ranges are chosen so no level ever has more rows than one page holds, which is what
    /// keeps the depth logarithmic rather than the width linear.
    /// </para>
    /// </remarks>
    private void AppendElements(
        List<DebugVariable> into, CorDebugArrayValue array, string path,
        int from, int to = int.MaxValue)
    {
        var count = Safe(() => (int?)array.Count) ?? 0;
        var rank = Safe(() => (int?)array.Rank) ?? 1;
        var dimensions = rank > 1 ? Safe(() => array.GetDimensions(rank)) : null;
        var page = Math.Max(1, _display.MaxChildren);

        // The array may have been reallocated since the range row was handed out, so the span is
        // clamped to what is actually there rather than trusted.
        AppendElementRange(into, array, path, Math.Min(from, count), Math.Min(to, count), page, dimensions);
    }

    /// <summary>Lists elements <c>[from, to)</c>, as values when they fit on one page and as
    /// sub-ranges when they do not.</summary>
    private void AppendElementRange(
        List<DebugVariable> into, CorDebugArrayValue array, string path,
        int from, int to, int page, int[]? dimensions)
    {
        var total = to - from;
        if (total <= 0)
            return;

        if (total <= page)
        {
            for (var i = from; i < to; i++)
            {
                var element = Safe(() => array.GetElementAtPosition(i));
                if (element is null)
                    continue;
                var name = dimensions is null
                    ? $"[{i}]"
                    : $"[{string.Join(",", IndicesOf(i, dimensions))}]";
                into.Add(Row(name, element, "element", $"{path}{name}"));
            }
            return;
        }

        // The largest power-of-page step that still splits this span into at most one page of
        // rows. Whole powers keep the boundaries at round numbers, so [1000..1999] rather than
        // [937..1836] — a range the user can predict is a range they can navigate.
        var step = page;
        while (total / step > page)
            step *= page;

        for (var start = from; start < to; start += step)
        {
            var end = Math.Min(to, start + step);
            into.Add(new DebugVariable
            {
                Name = $"[{start}..{end - 1}]",
                Value = $"{end - start} elements",
                Kind = "element",
                Type = string.Empty,
                VariablesReference = $"{path}.{MoreMarker}:{start}:{end}",
            });
        }
    }

    /// <summary>A linear element position as the per-dimension indices C# would write, row-major
    /// as the runtime stores them.</summary>
    private static int[] IndicesOf(int position, int[] dimensions)
    {
        var indices = new int[dimensions.Length];
        for (var d = dimensions.Length - 1; d >= 0; d--)
        {
            var length = Math.Max(1, dimensions[d]);
            indices[d] = position % length;
            position /= length;
        }
        return indices;
    }

    /// <summary>
    /// Lists an object's instance fields and properties, applying <c>DebuggerBrowsable</c>.
    /// </summary>
    /// <remarks>
    /// Fields first — they read without running the debuggee. Properties follow, VS-style: an
    /// auto-property already stands in the list as its backing field, so only computed getters
    /// are evaluated, and one that throws shows the failure in place of a value. A Raw View
    /// expansion lists neither properties nor hidden members.
    /// </remarks>
    /// <returns>Whether the list is exactly the object's own fields — false when something was
    /// hidden, inlined, or computed, which is what decides whether Raw View is worth offering.</returns>
    private bool AppendFields(
        List<DebugVariable> into, CorDebugValue value, string path, bool applyBrowsable, bool includeProperties)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return true;

        var complete = true;
        var budget = Math.Max(1, _display.MaxChildren);
        // A name is listed once: the nearest type's member wins over a shadowed base member, and
        // a property whose backing field is already shown is not evaluated again.
        var emitted = new HashSet<string>(into.Select(row => row.Name), StringComparer.Ordinal);

        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            // Browsable states declared on the type's properties, which also govern the
            // backing-field rows that stand in for auto-properties.
            var propertyStates = new Dictionary<string, BrowsableState>(StringComparer.Ordinal);
            if (applyBrowsable)
            {
                foreach (var property in Properties(metadata, typeDef))
                {
                    var declared = Safe<GetPropertyPropsResult?>(() => metadata.GetPropertyProps(property));
                    if (declared is null || string.IsNullOrEmpty(declared.Value.szProperty))
                        continue;
                    propertyStates[declared.Value.szProperty] =
                        DebuggerAttributes.BrowsableOf(metadata, property);
                }
            }

            foreach (var field in Fields(metadata, typeDef))
            {
                if (into.Count >= budget)
                    return false;

                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is null)
                    continue;
                var attributes = props.Value.pdwAttr;
                // Statics need a frame to read and constants have no storage at all; neither is
                // part of this instance's state.
                if (attributes.HasFlag(CorFieldAttr.fdStatic) || attributes.HasFlag(CorFieldAttr.fdLiteral))
                    continue;

                var name = DisplayFieldName(props.Value.szField);
                var state = applyBrowsable
                    ? DebuggerAttributes.BrowsableOf(metadata, field)
                    : BrowsableState.Collapsed;
                if (applyBrowsable && name != props.Value.szField &&
                    propertyStates.TryGetValue(name, out var propertyState))
                    state = propertyState;
                if (state == BrowsableState.Never)
                {
                    complete = false;
                    continue;
                }

                var member = Safe(() => obj.GetFieldValue(cls.Raw, field));
                if (member is null)
                    continue;

                if (state == BrowsableState.RootHidden)
                {
                    // The member vanishes and its own children take its place — how a List<T>
                    // shows elements rather than the array it keeps them in.
                    complete = false;
                    foreach (var grandchild in ChildrenOf(member, $"{path}.{name}"))
                    {
                        if (into.Count >= budget)
                            return false;
                        into.Add(grandchild);
                    }
                    continue;
                }

                if (!emitted.Add(name))
                {
                    complete = false;
                    continue;
                }
                into.Add(Row(name, member, "field", $"{path}.{name}"));
            }

            if (!includeProperties)
                continue;

            foreach (var property in Properties(metadata, typeDef))
            {
                if (into.Count >= budget)
                    return false;

                var props = Safe<GetPropertyPropsResult?>(() => metadata.GetPropertyProps(property));
                if (props is null || props.Value.pmdGetter.Rid == 0)
                    continue;

                var name = props.Value.szProperty;
                // Indexers need an index to mean anything, and a static getter is not this
                // instance's state.
                if (string.IsNullOrEmpty(name) || name == "Item" || emitted.Contains(name))
                    continue;
                if (Safe(() => metadata.GetMethodProps(props.Value.pmdGetter).pdwAttr.HasFlag(CorMethodAttr.mdStatic)) == true)
                    continue;

                var state = propertyStates.TryGetValue(name, out var declaredState)
                    ? declaredState
                    : BrowsableState.Collapsed;
                if (state == BrowsableState.Never)
                {
                    complete = false;
                    continue;
                }

                // A computed row means the list is no longer "exactly the fields".
                complete = false;
                emitted.Add(name);

                var member = MemberValue(value, name, callOnly: false, out var error);
                if (member is null)
                {
                    into.Add(new DebugVariable
                    {
                        Name = name,
                        Value = error.Length == 0 ? "could not be evaluated" : error,
                        Kind = "property",
                    });
                    continue;
                }

                if (state == BrowsableState.RootHidden)
                {
                    foreach (var grandchild in ChildrenOf(member, $"{path}.{name}"))
                    {
                        if (into.Count >= budget)
                            return false;
                        into.Add(grandchild);
                    }
                    continue;
                }

                into.Add(Row(name, member, "property", $"{path}.{name}"));
            }
        }

        return complete;
    }

    /// <summary>
    /// Lists the members of the value's <c>DebuggerTypeProxy</c>, which is what the type's author
    /// wants seen in place of its fields.
    /// </summary>
    private void AppendProxyMembers(List<DebugVariable> into, CorDebugValue value, string path)
    {
        var proxy = ProxyValue(value, out var error);
        if (proxy is null)
        {
            into.Add(new DebugVariable
            {
                Name = "Proxy unavailable",
                Value = error.Length == 0 ? "the debugger view type could not be constructed" : error,
                Kind = "diagnostic",
            });
            return;
        }

        var budget = Math.Max(1, _display.MaxChildren);

        foreach (var (_, metadata, typeDef) in TypeChain(proxy))
        {
            foreach (var property in Properties(metadata, typeDef))
            {
                if (into.Count >= budget)
                    return;

                var props = Safe<GetPropertyPropsResult?>(() => metadata.GetPropertyProps(property));
                if (props is null || props.Value.pmdGetter.Rid == 0)
                    continue;

                var name = props.Value.szProperty;
                if (string.IsNullOrEmpty(name))
                    continue;

                var state = _display.Browsable
                    ? DebuggerAttributes.BrowsableOf(metadata, property)
                    : BrowsableState.Collapsed;
                if (state == BrowsableState.Never)
                    continue;

                var member = MemberValue(proxy, name, callOnly: false, out _);
                if (member is null)
                    continue;

                var childPath = $"{path}.{ProxyMarker}.{name}";
                if (state == BrowsableState.RootHidden)
                {
                    foreach (var grandchild in ChildrenOf(member, childPath))
                    {
                        if (into.Count >= budget)
                            return;
                        into.Add(grandchild);
                    }
                    continue;
                }

                into.Add(Row(name, member, "proxy", childPath));
            }
        }
    }

    /// <summary>A frame's arguments and locals, as expandable rows — led, on an exception stop,
    /// by the thrown exception itself.</summary>
    /// <remarks>
    /// What the compiler did to the frame is undone here, the way VS undoes it: a lambda's capture
    /// class dissolves into the locals it captured, an async or iterator frame's state machine
    /// dissolves into the hoisted locals and parameters it carries, and the compiler's own
    /// temporaries stay out of the list entirely.
    /// </remarks>
    private List<DebugVariable> FrameVariables(CorDebugILFrame ilFrame)
    {
        _inspectionFrame = ilFrame;
        var variables = new List<DebugVariable>();
        if (CurrentExceptionValue() is { } exception)
            variables.Add(Row(ExceptionMarker, exception, "exception", ExceptionMarker));
        if (ReturnValueRow(ilFrame) is { } returned)
            variables.Add(returned);

        var (argNames, localNames) = FrameSymbolNames(ilFrame);
        var slots = new List<(string Name, CorDebugValue Value, string Kind)>();
        CollectSlots(slots, "arg", Safe(() => ilFrame.Arguments), argNames);
        CollectSlots(slots, "local", Safe(() => ilFrame.LocalVariables), localNames);

        foreach (var (name, value, kind) in slots)
        {
            if (name == "this" && IsStateMachine(value))
            {
                HoistStateMachine(value, variables);
                continue;
            }
            if (IsCompilerGeneratedName(name))
            {
                if (IsDisplayClass(value))
                    HoistDisplayClass(name, value, variables);
                continue;
            }
            variables.Add(Row(name, value, kind, name));
        }
        return variables;
    }

    private static void CollectSlots(
        List<(string Name, CorDebugValue Value, string Kind)> into,
        string kind, CorDebugValue[]? values, Dictionary<int, string> names)
    {
        if (values is null)
            return;
        for (var i = 0; i < values.Length; i++)
            into.Add((names.TryGetValue(i, out var symbol) ? symbol : $"{kind}{i}", values[i], kind));
    }

    private static bool IsCompilerGeneratedName(string name) =>
        name.StartsWith("CS$", StringComparison.Ordinal) || name.StartsWith("<>", StringComparison.Ordinal);

    /// <summary>Whether a frame's <c>this</c> is an async or iterator state machine rather than
    /// the object the user wrote — the "&lt;Foo&gt;d__2" the compiler moved the method into.</summary>
    private static bool IsStateMachine(CorDebugValue value) =>
        TypeNameOf(value).Contains(">d__", StringComparison.Ordinal);

    private static bool IsDisplayClass(CorDebugValue value) =>
        TypeNameOf(value).Contains("c__DisplayClass", StringComparison.Ordinal);

    /// <summary>The source-level local a hoisted state-machine field stands for —
    /// <c>&lt;total&gt;5__1</c> is the user's <c>total</c> — or null for any other field.</summary>
    private static string? HoistedLocalName(string field)
    {
        if (field.Length < 4 || field[0] != '<')
            return null;
        var close = field.IndexOf('>');
        return close > 1 && close + 1 < field.Length && char.IsDigit(field[close + 1])
            ? field[1..close]
            : null;
    }

    /// <summary>Presents a state machine's fields as the frame the user wrote: hoisted locals and
    /// parameters under their own names, the captured <c>this</c> as <c>this</c>, and the
    /// machinery (state, builder, awaiters) not at all.</summary>
    private void HoistStateMachine(CorDebugValue machine, List<DebugVariable> into)
    {
        var target = Safe(() => Dereference(machine));
        if (target is not CorDebugObjectValue obj)
            return;

        foreach (var (cls, metadata, typeDef) in TypeChain(machine))
        {
            foreach (var field in Fields(metadata, typeDef))
            {
                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is null || props.Value.pdwAttr.HasFlag(CorFieldAttr.fdStatic))
                    continue;
                var fieldName = props.Value.szField;
                var value = Safe(() => obj.GetFieldValue(cls.Raw, field));
                if (value is null)
                    continue;

                if (fieldName == "<>4__this")
                    into.Add(Row("this", value, "arg", $"this.{fieldName}"));
                else if (HoistedLocalName(fieldName) is { } source)
                    into.Add(Row(source, value, "local", $"this.{fieldName}"));
                else if (!fieldName.StartsWith("<", StringComparison.Ordinal) &&
                         !fieldName.StartsWith("CS$", StringComparison.Ordinal))
                    into.Add(Row(fieldName, value, "arg", $"this.{fieldName}"));
            }
            // Only the machine's own fields; its base holds nothing of the user's.
            break;
        }
    }

    /// <summary>Presents a capture class's fields as the captured locals they are, under the names
    /// the user gave them.</summary>
    private void HoistDisplayClass(string localName, CorDebugValue value, List<DebugVariable> into)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return;

        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
            foreach (var field in Fields(metadata, typeDef))
            {
                var props = Safe<GetFieldPropsResult?>(() => metadata.GetFieldProps(field));
                if (props is null || props.Value.pdwAttr.HasFlag(CorFieldAttr.fdStatic))
                    continue;
                var fieldName = props.Value.szField;
                var member = Safe(() => obj.GetFieldValue(cls.Raw, field));
                if (member is null)
                    continue;

                if (fieldName == "<>4__this")
                {
                    if (!into.Any(row => row.Name == "this"))
                        into.Add(Row("this", member, "arg", $"{localName}.{fieldName}"));
                }
                else if (!fieldName.StartsWith("<", StringComparison.Ordinal) &&
                         !fieldName.StartsWith("CS$", StringComparison.Ordinal))
                {
                    into.Add(Row(fieldName, member, "local", $"{localName}.{fieldName}"));
                }
            }
            break;
        }
    }

    private DebugVariable Row(string name, CorDebugValue value, string kind, string path)
    {
        var type = TypeNameOf(value);

        // DebuggerDisplay's Name=/Type= relabel a value's entry in a collection view — the row a
        // dictionary shows as "[key]" — never a variable, which keeps the name the user typed.
        if (kind is "element" or "proxy" && _display.DebuggerDisplay && _displayDepth < MaxDisplayDepth &&
            DisplayAttributeFor(value) is { } display)
        {
            if (display.Name is { Length: > 0 } nameFormat)
                name = RenderDisplayFormat(value, nameFormat);
            if (display.Type is { Length: > 0 } typeFormat)
                type = RenderDisplayFormat(value, typeFormat);
        }

        return new DebugVariable
        {
            Name = name,
            Value = DescribeValue(value),
            Kind = kind,
            Type = type,
            VariablesReference = Expandable(value) ? path : string.Empty,
            Settable = kind is "field" or "element" or "local" or "arg",
        };
    }

    /// <summary>
    /// Whether a value has anything worth expanding — asked without reading the children, since
    /// this is answered for every row of every list.
    /// </summary>
    private bool Expandable(CorDebugValue value)
    {
        var target = Safe(() => Dereference(value));
        return target switch
        {
            null => false,
            CorDebugStringValue => false,
            CorDebugArrayValue array => (Safe(() => (int?)array.Count) ?? 0) > 0,
            CorDebugObjectValue => true,
            _ => false,
        };
    }

    /// <summary>Auto-property backing fields are shown under the property's name, which is also
    /// the name that resolves back to them.</summary>
    private static string DisplayFieldName(string field)
    {
        if (field.StartsWith('<') && field.Contains(">k__BackingField", StringComparison.Ordinal))
            return field[1..field.IndexOf('>')];
        return field;
    }

    // --- DebuggerDisplay ------------------------------------------------------------------------

    /// <summary>
    /// The display string the value's type asks for, or null when it declares none (or the
    /// attribute is switched off).
    /// </summary>
    private string? DisplayStringFor(CorDebugValue value)
    {
        if (!_display.DebuggerDisplay || _displayDepth >= MaxDisplayDepth)
            return null;

        var format = DisplayAttributeFor(value)?.Value;
        if (format is null || format.Length == 0)
            return null;

        return RenderDisplayFormat(value, format);
    }

    /// <summary>The nearest <c>DebuggerDisplay</c> in the value's type chain, named arguments and
    /// all.</summary>
    private static DebuggerAttributes.DisplayAttribute? DisplayAttributeFor(CorDebugValue value)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            var display = Safe(() => DebuggerAttributes.DisplayOf(metadata, typeDef));
            if (display is not null)
                return display;
        }
        return null;
    }

    /// <summary>Renders one <c>DebuggerDisplay</c> format string — the value format, or a
    /// <c>Name</c>/<c>Type</c> named argument, which use the same grammar.</summary>
    private string RenderDisplayFormat(CorDebugValue value, string format)
    {
        _displayDepth++;
        try
        {
            var rendered = new StringBuilder();
            foreach (var part in DebuggerDisplayFormat.Parse(format))
            {
                if (!part.IsExpression)
                {
                    rendered.Append(part.Text);
                    continue;
                }

                var member = ResolveRelative(value, part.Text, out var error);
                if (member is null)
                {
                    // Showing the failure beats showing a half-rendered string that reads like
                    // real data.
                    rendered.Append('{').Append(part.Text).Append(error.Length == 0 ? "" : ": " + error).Append('}');
                    continue;
                }

                var text = DescribeValue(member);
                if (part.NoQuotes && text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                    text = text[1..^1];
                rendered.Append(text);
            }
            return rendered.ToString();
        }
        finally
        {
            _displayDepth--;
        }
    }

    /// <summary>
    /// Resolves a dotted expression against an object rather than against a frame — which is what
    /// a <c>DebuggerDisplay</c> placeholder is: a member path rooted at the instance.
    /// </summary>
    private CorDebugValue? ResolveRelative(CorDebugValue root, string expression, out string error)
    {
        error = string.Empty;

        var text = expression.Replace(" ", string.Empty);
        if (text.StartsWith("this.", StringComparison.Ordinal))
            text = text["this.".Length..];
        if (text is "this")
            return root;

        CorDebugValue? current = root;
        foreach (var segment in text.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var (name, indexes, isCall) = ParseSegment(segment);
            current = MemberValue(current!, name, isCall, out error);
            if (current is null)
                return null;

            foreach (var index in indexes)
            {
                current = IndexValue(current, index, out var indexError);
                if (current is null)
                {
                    error = indexError.Length > 0 ? indexError : $"cannot index into '{name}'";
                    return null;
                }
            }
        }
        return current;
    }

    // --- DebuggerTypeProxy ----------------------------------------------------------------------

    private string? ProxyTypeNameOf(CorDebugValue value) =>
        FirstAttributeInChain(value, DebuggerAttributes.TypeProxy);

    /// <summary>
    /// Constructs the value's proxy in the debuggee: <c>new View(theObject)</c>, run by the
    /// target itself.
    /// </summary>
    /// <remarks>
    /// There is no cheaper way. The proxy's properties are ordinary code — <c>Items</c> on a
    /// dictionary's view walks the buckets and builds an array — so an object has to exist to run
    /// them against. That means a function evaluation, with every consequence one carries: the
    /// debuggee runs, and a proxy whose constructor throws or blocks reports a failure here rather
    /// than a view.
    /// </remarks>
    private CorDebugValue? ProxyValue(CorDebugValue value, out string error)
    {
        error = string.Empty;

        var typeName = ProxyTypeNameOf(value);
        if (typeName is null)
            return null;

        var normalized = DebuggerAttributes.NormalizeTypeName(typeName);
        var proxyType = FindTypeDef(value, normalized);
        if (proxyType is null)
        {
            error = $"the view type '{normalized}' was not found in any loaded module";
            return null;
        }

        var (module, typeDef) = proxyType.Value;
        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
        if (metadata is null)
        {
            error = "the view type's metadata could not be read";
            return null;
        }

        var ctorToken = Safe(() =>
        {
            var found = metadata.FindMethod(typeDef, ".ctor", IntPtr.Zero, 0);
            return (mdMethodDef?)found;
        });
        if (ctorToken is not { } ctor)
        {
            error = $"'{normalized}' has no single-argument constructor to build the view with";
            return null;
        }

        var function = Safe(() => module.GetFunctionFromToken(ctor));
        if (function is null)
        {
            error = "the view type's constructor could not be resolved";
            return null;
        }

        // A generic proxy (the usual shape for a collection view) has to be instantiated with the
        // same type arguments as the value it is viewing; a non-generic one takes the plain path.
        var typeArguments = normalized.Contains('`', StringComparison.Ordinal)
            ? TypeArgumentsOf(value)
            : null;

        return RunEval(
            eval =>
            {
                if (typeArguments is { Length: > 0 })
                    eval.NewParameterizedObject(function.Raw, typeArguments.Length, typeArguments, 1, [value.Raw]);
                else
                    eval.NewObject(function.Raw, 1, [value.Raw]);
            },
            out error);
    }

    /// <summary>The value's own generic arguments, which a generic proxy is instantiated with.</summary>
    private static ICorDebugType[]? TypeArgumentsOf(CorDebugValue value)
    {
        var exact = Safe(() => Dereference(value) is CorDebugObjectValue obj ? obj.ExactType : null);
        var parameters = exact is null ? null : Safe(() => exact.TypeParameters);
        return parameters is { Length: > 0 }
            ? parameters.Select(p => p.Raw).ToArray()
            : null;
    }

    /// <summary>
    /// Locates a type by name, starting in the value's own module — where a proxy declared beside
    /// its type lives — and falling back to every loaded module.
    /// </summary>
    private (CorDebugModule Module, mdTypeDef TypeDef)? FindTypeDef(CorDebugValue value, string typeName)
    {
        var own = Safe(() => (Dereference(value) as CorDebugObjectValue)?.Class?.Module);
        if (own is not null && TypeDefIn(own, typeName) is { } here)
            return (own, here);

        foreach (var module in LoadedModules())
        {
            if (TypeDefIn(module, typeName) is { } found)
                return (module, found);
        }
        return null;
    }

    private static mdTypeDef? TypeDefIn(CorDebugModule module, string typeName)
    {
        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
        if (metadata is null)
            return null;

        // A nested proxy is stored as "Outer+Inner" but indexed under the nested name alone.
        var candidates = typeName.Contains('+', StringComparison.Ordinal)
            ? new[] { typeName, typeName.Replace('+', '.'), typeName[(typeName.LastIndexOf('+') + 1)..] }
            : [typeName];

        foreach (var candidate in candidates)
        {
            var token = Safe(() =>
            {
                var found = metadata.FindTypeDefByName(candidate, default);
                return (mdTypeDef?)found;
            });
            if (token is { } typeDef && typeDef.Rid != 0)
                return typeDef;
        }
        return null;
    }

    // --- metadata walking -----------------------------------------------------------------------

    /// <summary>
    /// The value's type and each of its base types, with the metadata reader that describes it.
    /// </summary>
    /// <remarks>
    /// Walked through the runtime's exact type, whose <c>Base</c> crosses assembly boundaries —
    /// the metadata <c>extends</c> token cannot, and a debuggee where <c>Order : EntityBase</c>
    /// lives in another project (or derives from <c>Exception</c>) hides every inherited field
    /// when the walk stops at the module edge.
    /// </remarks>
    private static IEnumerable<(CorDebugClass Class, MetaDataImport Metadata, mdTypeDef TypeDef)> TypeChain(
        CorDebugValue value)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            yield break;

        var type = Safe(() => obj.ExactType);
        if (type is not null)
        {
            for (var depth = 0; type is not null && depth < MaxTypeDepth; depth++)
            {
                var cls = Safe(() => type.Class);
                if (cls is null)
                    yield break;
                var metadata = Safe(() => cls.Module) is { } module
                    ? Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module))
                    : null;
                if (metadata is null)
                    yield break;

                yield return (cls, metadata, cls.Token);

                var current = type;
                type = Safe(() => current.Base);
            }
            yield break;
        }

        // No exact type (a runtime that cannot answer, mid-teardown): the module-bounded
        // metadata walk still describes the leaf type.
        var leaf = Safe(() => obj.Class);
        for (var depth = 0; leaf is not null && depth < MaxTypeDepth; depth++)
        {
            var module = Safe(() => leaf.Module);
            if (module is null)
                yield break;
            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                yield break;

            yield return (leaf, metadata, leaf.Token);

            var current = leaf;
            leaf = Safe(() =>
            {
                var props = metadata.GetTypeDefProps(current.Token);
                var extends = props.ptkExtends;
                return extends.Type == CorTokenType.mdtTypeDef && extends.Rid != 0
                    ? module.GetClassFromToken((mdTypeDef)extends)
                    : null;
            });
        }
    }

    /// <summary>The first type in the chain that carries <paramref name="attributeName"/>, as its
    /// string argument. Attributes are inherited in the debugger's view, nearest type first.</summary>
    private static string? FirstAttributeInChain(CorDebugValue value, string attributeName)
    {
        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            var argument = Safe(() => DebuggerAttributes.StringArgument(metadata, typeDef, attributeName));
            if (argument is { Length: > 0 })
                return argument;
        }
        return null;
    }

    private static IEnumerable<mdFieldDef> Fields(MetaDataImport metadata, mdTypeDef typeDef)
    {
        var handle = IntPtr.Zero;
        var buffer = new mdFieldDef[64];
        try
        {
            while (true)
            {
                var read = 0;
                try { read = metadata.EnumFields(ref handle, typeDef, buffer); }
                catch { read = 0; }
                if (read <= 0)
                    yield break;
                for (var i = 0; i < read; i++)
                    yield return buffer[i];
                if (read < buffer.Length)
                    yield break;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
                Try(() => metadata.CloseEnum(handle));
        }
    }

    private static IEnumerable<mdProperty> Properties(MetaDataImport metadata, mdTypeDef typeDef)
    {
        var handle = IntPtr.Zero;
        var buffer = new mdProperty[64];
        try
        {
            while (true)
            {
                var read = 0;
                try { read = metadata.EnumProperties(ref handle, typeDef, buffer); }
                catch { read = 0; }
                if (read <= 0)
                    yield break;
                for (var i = 0; i < read; i++)
                    yield return buffer[i];
                if (read < buffer.Length)
                    yield break;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
                Try(() => metadata.CloseEnum(handle));
        }
    }

    /// <summary>The value's type name, for the Type column.</summary>
    private static string TypeNameOf(CorDebugValue value)
    {
        var target = Safe(() => Dereference(value));
        if (target is CorDebugStringValue)
            return "string";
        if (target is CorDebugArrayValue array)
        {
            var rank = Safe(() => (int?)array.Rank) ?? 1;
            return ElementTypeNameOf(array) + "[" + new string(',', Math.Max(0, rank - 1)) + "]";
        }

        // The exact type spells the instantiation — "List<int>", not the metadata's "List`1".
        if (Safe(() => (target ?? value).ExactType) is { } exact)
        {
            var exactName = NameOfType(exact);
            // The raw element-kind fallbacks ("Class", "ValueType") say less than the metadata
            // walk below, so only a real name short-circuits.
            if (exactName.Length > 0 && exactName != "Class" && exactName != "ValueType")
                return exactName;
        }

        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            var name = Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef);
            if (name is { Length: > 0 })
                return name;
        }
        return Safe(() => (target ?? value).Type.ToString()) ?? string.Empty;
    }

    /// <summary>The element type of an array, as C# would write it — "SZArray" says nothing.</summary>
    private static string ElementTypeNameOf(CorDebugArrayValue array)
    {
        var element = Safe(() => array.ExactType?.FirstTypeParameter);
        return element is null ? "object" : NameOfType(element);
    }

    private static string NameOfType(CorDebugType type)
    {
        var element = Safe(() => (CorElementType?)type.Type);
        var primitive = element switch
        {
            CorElementType.Boolean => "bool",
            CorElementType.Char => "char",
            CorElementType.I1 => "sbyte",
            CorElementType.U1 => "byte",
            CorElementType.I2 => "short",
            CorElementType.U2 => "ushort",
            CorElementType.I4 => "int",
            CorElementType.U4 => "uint",
            CorElementType.I8 => "long",
            CorElementType.U8 => "ulong",
            CorElementType.R4 => "float",
            CorElementType.R8 => "double",
            CorElementType.String => "string",
            CorElementType.Object => "object",
            _ => null,
        };
        if (primitive is not null)
            return primitive;

        if (element is CorElementType.SZArray or CorElementType.Array)
        {
            var inner = Safe(() => type.FirstTypeParameter);
            return inner is null ? "object[]" : NameOfType(inner) + "[]";
        }

        var cls = Safe(() => type.Class);
        var metadata = cls is null ? null : Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(cls.Module));
        var name = metadata is null ? null : Safe(() => metadata.GetTypeDefProps(cls!.Token).szTypeDef);
        return name is { Length: > 0 } ? WithTypeArguments(name, type) : element?.ToString() ?? "object";
    }

    /// <summary>Replaces a metadata arity suffix with the instantiation it stands for:
    /// <c>List`1</c> plus <c>[int]</c> is <c>List&lt;int&gt;</c>.</summary>
    private static string WithTypeArguments(string name, CorDebugType type)
    {
        var tick = name.IndexOf('`');
        if (tick < 0)
            return name;

        var arguments = Safe(() => type.TypeParameters);
        if (arguments is not { Length: > 0 })
            return name;

        return name[..tick] + "<" + string.Join(", ", arguments.Select(NameOfType)) + ">";
    }

    // --- Just My Code ---------------------------------------------------------------------------

    /// <summary>
    /// How many times one step may step back out of somebody else's code before giving up and
    /// stopping wherever it landed.
    /// </summary>
    /// <remarks>
    /// A bound rather than a loop: stepping into a property that is a chain of framework calls can
    /// need several, but an unbounded retry would turn a step into a run when the "user code" test
    /// never becomes true.
    /// </remarks>
    private const int MaxStepOuts = 32;

    // The runtime's own Just My Code is used when it can be — see MarkJustMyCode — but never
    // relied on. It needs every module marked through SetJMCStatus first, and an optimized or
    // NGen'd image cannot be marked at all, so a process always has some code the runtime will
    // happily stop in. Filtering the step completes here as well is what covers that, and it is
    // also the whole of the behaviour when no solution says which assemblies are the user's.

    private int _stepOutBudget;

    /// <summary>
    /// Whether the thread stopped somewhere the user wrote — which is what decides whether a step
    /// complete is reported or quietly stepped back out of.
    /// </summary>
    /// <remarks>
    /// Three separate ways to be somebody else's code, all of which VS treats identically: a
    /// framework or GAC module, a method (or its declaring type) marked <c>DebuggerStepThrough</c>,
    /// <c>DebuggerHidden</c>, or <c>DebuggerNonUserCode</c>, and code with no symbols at all —
    /// stopping there shows a call stack and no source, which is never what a step was for.
    /// </remarks>
    private bool IsUserFrame(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                return false;

            var function = Safe(() => frame.Function);
            var module = Safe(() => function?.Module);
            var moduleName = Safe(() => module?.Name) ?? string.Empty;
            if (moduleName.Length == 0 || !IsUserModule(moduleName))
                return false;

            // No sequence point for the current IP means no source to show.
            if (FrameLocation(frame).File.Length == 0)
                return false;

            var metadata = Safe(() => module is null ? null : Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                return true;

            var methodToken = Safe(() => frame.FunctionToken);
            if (methodToken is { } method && IsMarkedNonUser(metadata, method))
                return false;

            var declaringType = Safe(() =>
                methodToken is { } m ? (mdTypeDef?)metadata.GetMethodProps(m).pClass : null);
            if (declaringType is { } type)
            {
                if (IsMarkedNonUser(metadata, type))
                    return false;

                if (IsAwaitPlumbing(Safe(() => metadata.GetTypeDefProps(type).szTypeDef)))
                    return false;
            }

            return true;
        }
        catch
        {
            // Unreadable frames are not somewhere to strand the user, so treat them as stoppable.
            return true;
        }
    }

    /// <summary>
    /// Namespaces that exist only to make <c>await</c> and <c>yield</c> work.
    /// </summary>
    /// <remarks>
    /// Matched by namespace rather than by a list of type names because the set is not fixed:
    /// the builders differ between <c>Task</c>, <c>ValueTask</c>, <c>IAsyncEnumerable</c> and every
    /// custom builder a library defines, and a list would be out of date the first time one of
    /// them changed.
    /// </remarks>
    private static readonly string[] AwaitPlumbingNamespaces =
    [
        "System.Runtime.CompilerServices.",
        "System.Threading.Tasks.",
        "System.Runtime.ExceptionServices.",
    ];

    /// <summary>
    /// Whether a type is await machinery rather than anybody's code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stepping over an <c>await</c> otherwise walks the user through the builder, the awaiter and
    /// the continuation before it reaches the next line they wrote — several presses of Step Over
    /// that land nowhere they recognise. This is what makes a single press do what it looks like
    /// it should.
    /// </para>
    /// <para>
    /// Deliberately does not match the compiler-generated <c>&lt;Method&gt;d__N</c> state machine
    /// in the user's own assembly. That type carries the user's method body, with their sequence
    /// points, and skipping it would mean an async method could not be stepped through at all.
    /// </para>
    /// </remarks>
    private static bool IsAwaitPlumbing(string? typeName)
    {
        if (typeName is not { Length: > 0 })
            return false;

        return AwaitPlumbingNamespaces.Any(
            ns => typeName.StartsWith(ns, StringComparison.Ordinal));
    }

    private static bool IsMarkedNonUser(MetaDataImport metadata, mdToken token) =>
        IsMarkedWith(metadata, token, DebuggerAttributes.StepOverMarkers);

    private static bool IsMarkedWith(MetaDataImport metadata, mdToken token, string[] markers) =>
        markers.Any(marker => Safe(() => DebuggerAttributes.Has(metadata, token, marker)) == true);

    /// <summary>
    /// Whether a stack frame is plumbing the user should not have to read past.
    /// </summary>
    /// <remarks>
    /// A narrower question than <see cref="IsUserFrame"/> asks. Stepping declines to stop in
    /// anything marked <c>DebuggerStepThrough</c>; a call stack still has to show it, because a
    /// breakpoint set inside such a method does stop and the user then needs to see where they
    /// are. Only <c>DebuggerHidden</c> and <c>DebuggerNonUserCode</c> — and modules that are not
    /// the user's at all — are frames worth folding away.
    /// </remarks>
    private bool IsNonUserFrame(CorDebugFrame frame)
    {
        try
        {
            var function = Safe(() => frame.Function);
            var module = Safe(() => function?.Module);
            var moduleName = Safe(() => module?.Name) ?? string.Empty;
            if (moduleName.Length == 0 || !IsUserModule(moduleName))
                return true;

            if (module is null)
                return false;

            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                return false;

            var methodToken = Safe(() => (mdToken?)function!.Token);
            if (methodToken is not { } method)
                return false;

            if (IsMarkedWith(metadata, method, DebuggerAttributes.HiddenMarkers))
                return true;

            var declaringType = Safe(() => (mdToken?)metadata.GetMethodProps(new mdMethodDef(method.Value)).pClass);
            if (declaringType is not { } type)
                return false;

            return IsMarkedWith(metadata, type, DebuggerAttributes.HiddenMarkers) ||
                   IsAwaitPlumbing(Safe(() => metadata.GetTypeDefProps(new mdTypeDef(type.Value)).szTypeDef));
        }
        catch
        {
            // A frame that cannot be read is not one to hide — the user would be left with a gap
            // and no way to find out what was in it.
            return false;
        }
    }

    /// <summary>
    /// Steps back out of a frame the user did not write, on the runtime's callback thread.
    /// </summary>
    /// <returns>Whether a step was armed; false means the caller should report the stop as-is.</returns>
    private bool TryStepOutOfNonUserCode(CorDebugThread thread)
    {
        if (_stepOutBudget <= 0)
            return false;
        _stepOutBudget--;

        try
        {
            var stepper = thread.ActiveFrame.CreateStepper();
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            // Explicitly not a Just My Code stepper. This step exists precisely because the thread
            // is somewhere the user did not write, and a JMC step out of non-user code is a step
            // whose stopping condition is already false where it starts.
            SetStepperJustMyCode(stepper, false);
            stepper.StepOut();
            // Still the same logical step, so _steppingThreadId is left alone: this step-out is
            // how the original step continues, not a new one.
            lock (_stepperLock)
                _steppers.Add((ThreadId(thread), stepper));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly Lock _stepperLock = new();

    /// <summary>
    /// Says whether a stepper is filtering to the user's code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always said, never left to the default, and that is the point of it. A process has two kinds
    /// of step in flight — the one the user asked for and the one the engine issues to get back out
    /// of somebody else's code — and they want opposite answers. Setting only the first would leave
    /// the second on whatever the runtime happened to default to, which is the difference between a
    /// step-out that completes and one that does not.
    /// </para>
    /// <para>
    /// A refusal is not worth reporting: an older runtime without
    /// <c>ICorDebugStepper2</c> simply steps unfiltered, and the engine's own filtering of step
    /// completes is still there to do the same work more slowly.
    /// </para>
    /// </remarks>
    private static void SetStepperJustMyCode(CorDebugStepper stepper, bool justMyCode)
    {
        try { stepper.TrySetJMC(justMyCode); }
        catch { }
    }

    /// <summary>
    /// The statement a step started from: its thread, its method, the IL range the statement
    /// occupies, and whether the user asked to step into calls.
    /// </summary>
    /// <param name="FrameStart">The stack address the origin frame began at, which distinguishes
    /// one activation of a method from another. 0 when the runtime would not say, in which case
    /// the frame check is skipped rather than guessed at.</param>
    private sealed record StepOrigin(
        int ThreadId, int MethodToken, ulong FrameStart, COR_DEBUG_STEP_RANGE Range, bool StepInto);

    /// <summary>Where the in-flight step began, or null when no range step is outstanding.</summary>
    /// <remarks>Volatile because it is written by the session thread arming the step and read by
    /// the runtime's callback thread completing it.</remarks>
    private volatile StepOrigin? _stepOrigin;

    /// <summary>
    /// Re-arms the step that is in flight when stepping out of somebody else's code has landed
    /// back on the statement it started from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between a Step Into that works and one that looks broken. Stepping
    /// into a helper marked <c>DebuggerStepThrough</c> lands in code the user did not write; the
    /// Just My Code path steps out of it; and the step-out lands on the very line the step began
    /// on. Reported as-is, the user pressed Step Into and the debugger did not move.
    /// </para>
    /// <para>
    /// Re-issuing the original range step continues past the call instead. It shares the step-out
    /// budget, so a line calling twenty hidden helpers still terminates rather than looping.
    /// </para>
    /// </remarks>
    private bool TryResumeStepOverOrigin(CorDebugThread thread)
    {
        if (_stepOrigin is not { } origin)
            return false;
        if (_stepOutBudget <= 0)
            return false;
        if (ThreadId(thread) != origin.ThreadId)
            return false;

        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                return false;

            // A different method means real progress was made; only landing back inside the
            // original statement is the case this exists for.
            if ((Safe(() => (int?)frame.FunctionToken) ?? 0) != origin.MethodToken)
                return false;

            // And a different activation of the same method is progress too. A recursive call
            // enters the same method at an IL offset inside the caller's own statement range —
            // for a single-expression body, at offset 0 of the one range there is — so without
            // this a Step Into recursion reads as "did not move" and re-arms in the callee,
            // walking one level deeper per re-arm until the budget runs out.
            var frameStart = Safe(() => (ulong?)frame.StackRange.pStart) ?? 0;
            if (origin.FrameStart != 0 && frameStart != 0 && frameStart != origin.FrameStart)
                return false;

            var offset = Safe(() => (int?)frame.IP.pnOffset);
            if (offset is not { } ip || ip < origin.Range.startOffset || ip >= origin.Range.endOffset)
                return false;

            _stepOutBudget--;

            var stepper = frame.CreateStepper();
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            // The user's step continuing, not an internal one, so it is filtered exactly as the
            // step it re-arms was. Asked again rather than remembered: the re-arm happens in the
            // origin frame, which is the frame the answer is about.
            SetStepperJustMyCode(stepper, CanStepWithRuntimeJustMyCode(frame));
            stepper.SetRangeIL(true);
            stepper.StepRange(origin.StepInto, [origin.Range], 1);

            // Still the same logical step, so the stepping thread is left as it was.
            lock (_stepperLock)
                _steppers.Add((origin.ThreadId, stepper));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The thread the in-flight step was armed on, or 0 when no step is outstanding.
    /// </summary>
    private int _steppingThreadId;

    /// <summary>
    /// Cancels every armed stepper, because something other than a step decided where the target
    /// stops.
    /// </summary>
    /// <remarks>
    /// Without this, a step interrupted by a breakpoint or an exception leaves its stepper armed in
    /// the runtime. The next time the user continues, that stepper completes and the session
    /// reports a step nobody asked for — arriving, from the client's point of view, as the target
    /// stopping at random. Deactivating is also what makes the thread check on step completes
    /// meaningful: it bounds how long a stale stepper can survive.
    /// </remarks>
    private void DeactivateSteppers()
    {
        lock (_stepperLock)
        {
            foreach (var (_, stepper) in _steppers)
            {
                // A stepper whose thread or process has already gone throws here; there is nothing
                // to cancel in that case, which is the outcome we wanted anyway.
                try { _ = stepper.TryDeactivate(); }
                catch { }
            }
            _steppers.Clear();
        }
        _steppingThreadId = 0;

        // The step this origin belonged to is over, however it ended. Left set, it would let a
        // later stop be mistaken for that step landing back where it began.
        _stepOrigin = null;
        DisarmReturnProbes();
    }

    // --- return values --------------------------------------------------------------------------

    /// <summary>Pseudo-variable naming what the call the step just went over returned.</summary>
    internal const string ReturnMarker = "$return";

    /// <summary>
    /// How many calls on one line are worth watching.
    /// </summary>
    /// <remarks>
    /// Each one costs breakpoints armed and disarmed around every step, and a line with more calls
    /// than this is one where naming which return value is being shown stops being possible anyway.
    /// </remarks>
    private const int MaxReturnProbes = 8;

    /// <summary>A breakpoint armed where a call's return value is still readable.</summary>
    /// <param name="CallOffset">The IL offset of the call itself, which is what the runtime wants
    /// back when asked for the value — not the offset the breakpoint sits at.</param>
    private sealed record ReturnProbe(
        int ThreadId,
        int MethodToken,
        string ModuleName,
        int CallOffset,
        string Callee,
        CorDebugFunctionBreakpoint Breakpoint);

    /// <summary>Guarded by <see cref="_stepperLock"/>, like the steppers they belong to.</summary>
    private readonly List<ReturnProbe> _returnProbes = [];

    /// <summary>What the last step's call returned, held until the next step replaces it.</summary>
    private volatile CapturedReturn? _returnValue;

    /// <param name="Handle">A strong handle, so the value survives the continue that takes the step
    /// on to where it was going. Null for anything not on the heap.</param>
    /// <param name="Text">Set instead of <paramref name="Handle"/> for a value that cannot be
    /// handled — a primitive or a struct — read at the moment it was still live.</param>
    private sealed record CapturedReturn(
        int ThreadId, string Callee, CorDebugHandleValue? Handle, string Text, string Type);

    /// <summary>
    /// Arms a breakpoint wherever a call in this statement leaves its return value readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to "what did that just return?", which otherwise costs the user an edit to
    /// introduce a local, a rebuild, and the loss of the state they were looking at.
    /// </para>
    /// <para>
    /// Not a breakpoint at the callee's return: by the time the caller resumes, the value is in
    /// whatever register or stack slot the calling convention chose and nothing can name it. The
    /// runtime is asked instead — it knows where the value lives and for how long, and answers with
    /// the native offsets at which it can still be read.
    /// </para>
    /// </remarks>
    private void ArmReturnProbes(CorDebugFrame frame, int threadId, COR_DEBUG_STEP_RANGE range)
    {
        var function = Safe(() => frame.Function);
        var ilCode = Safe(() => function?.ILCode);
        var nativeCode = Safe(() => function?.NativeCode);
        if (function is null || ilCode is null || nativeCode is null)
            return;

        var size = Safe(() => (int?)ilCode.Size) ?? 0;
        if (size <= 0)
            return;

        var il = Safe(() => ilCode.GetCode(0, size, size));
        if (il is not { Length: > 0 })
            return;

        var token = Safe(() => (int?)function.Token) ?? 0;
        var moduleName = Safe(() => function.Module.Name) ?? string.Empty;
        var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(function.Module));

        foreach (var call in IlCallSites.Between(il, (int)range.startOffset, (int)range.endOffset))
        {
            // newobj's result is the object it just made, which is on the stack and named by the
            // expression — there is nothing here the user cannot already see.
            if (call.ConstructsAnObject)
                continue;

            int[] offsets;
            try
            {
                if (nativeCode.TryGetReturnValueLiveOffset(call.Offset, out offsets) != HRESULT.S_OK)
                    continue;
            }
            catch
            {
                // A void call has no live offset, and neither does a call the JIT inlined. Both are
                // ordinary and neither is worth narrating.
                continue;
            }

            var callee = CalleeName(metadata, call.Token);
            foreach (var native in offsets ?? [])
            {
                lock (_stepperLock)
                {
                    if (_returnProbes.Count >= MaxReturnProbes)
                        return;
                }

                try
                {
                    var breakpoint = nativeCode.CreateBreakpoint(native);
                    breakpoint.Activate(true);
                    lock (_stepperLock)
                    {
                        _returnProbes.Add(new ReturnProbe(
                            threadId, token, moduleName, call.Offset, callee, breakpoint));
                    }
                }
                catch
                {
                    // Nothing armed is nothing to clean up, and a step with no return value is the
                    // behaviour this replaced.
                }
            }
        }
    }

    /// <summary>The name of the method a call token names, for labelling the value it returned.</summary>
    private static string CalleeName(MetaDataImport? metadata, int token)
    {
        if (metadata is null || token == 0)
            return string.Empty;

        // MethodDef for a call within the module, MemberRef for one outside it — the two the C#
        // compiler emits, and the two that carry a name to read.
        var name = Safe(() => metadata.GetMethodProps(new mdMethodDef((uint)token)).szMethod)
            ?? Safe(() => metadata.GetMemberRefProps(new mdMemberRef((uint)token)).szMember);

        return name ?? string.Empty;
    }

    /// <summary>
    /// Reads and keeps the return value if this stop is one of the step's own probes.
    /// </summary>
    /// <returns>Whether this was a probe, in which case the caller must resume rather than report a
    /// stop — the user pressed Step, not Continue, and this breakpoint is the engine's own.</returns>
    private bool TryCaptureReturnValue(CorDebugThread thread, CorDebugBreakpoint? breakpoint)
    {
        ReturnProbe[] probes;
        lock (_stepperLock)
            probes = [.. _returnProbes];

        if (probes.Length == 0)
            return false;

        // By identity, and only against a breakpoint of the same kind — comparing across the
        // breakpoint types is an invalid cast inside the wrapper's own equality, not a false.
        if (breakpoint is not CorDebugFunctionBreakpoint hit)
            return false;

        if (probes.FirstOrDefault(p => p.Breakpoint.Equals(hit)) is not { } probe)
            return false;

        // A native breakpoint is armed in the code, not in a thread, so any thread running the same
        // method hits it — which for a web application serving concurrent requests is routine. It is
        // still the engine's own breakpoint and must still be resumed silently: reported as a stop
        // it would be a breakpoint the user never set, at a line they were not looking at, and it
        // would cancel the step they actually asked for.
        var threadId = ThreadId(thread);
        if (threadId != probe.ThreadId)
            return true;

        try
        {
            if (thread.ActiveFrame is CorDebugILFrame frame &&
                frame.TryGetReturnValueForILOffset(probe.CallOffset, out var value) == HRESULT.S_OK &&
                value is not null)
            {
                _returnValue = Capture(threadId, probe.Callee, value);
            }
        }
        catch
        {
            // The value could not be read where the runtime said it would be. Nothing to report,
            // and the step must still go on.
        }

        // Every probe for this step, not just this one: the value has been taken, and leaving the
        // rest armed would stop the step again at the next call on the same line.
        DisarmReturnProbes();
        return true;
    }

    /// <summary>
    /// Takes a value that is about to stop being readable and keeps what can be kept of it.
    /// </summary>
    /// <remarks>
    /// A handle where one can be made, because that keeps the object itself — expandable in the
    /// variables view like any other. Everything else is read to text on the spot, since a
    /// primitive lives in a register the continue is about to reuse. The text is read without
    /// display attributes: this runs on the runtime's callback thread in the middle of a step, and
    /// a <c>DebuggerDisplay</c> getter evaluated there runs the debuggee's own code at a moment
    /// nobody chose.
    /// </remarks>
    private CapturedReturn Capture(int threadId, string callee, CorDebugValue value)
    {
        var type = TypeNameOf(value);
        var heap = value is CorDebugReferenceValue reference && Safe(() => (bool?)reference.IsNull) != true
            ? Safe(() => reference.Dereference()) as CorDebugHeapValue
            : value as CorDebugHeapValue;

        if (heap is not null &&
            Safe(() => heap.TryCreateHandle(CorDebugHandleType.HANDLE_STRONG, out var h) == HRESULT.S_OK ? h : null)
                is { } handle)
        {
            return new CapturedReturn(threadId, callee, handle, string.Empty, type);
        }

        return new CapturedReturn(
            threadId, callee, null, DescribeValue(value, applyDisplay: false), type);
    }

    /// <summary>Removes the step's probes from the target, whether or not one of them fired.</summary>
    private void DisarmReturnProbes()
    {
        ReturnProbe[] probes;
        lock (_stepperLock)
        {
            if (_returnProbes.Count == 0)
                return;
            probes = [.. _returnProbes];
            _returnProbes.Clear();
        }

        foreach (var probe in probes)
        {
            try { probe.Breakpoint.Activate(false); }
            catch { }
        }
    }

    /// <summary>
    /// Forgets the value the previous step captured, and releases the handle holding it alive.
    /// </summary>
    /// <remarks>
    /// A strong handle is exactly as strong as it sounds: left behind, it keeps the returned object
    /// out of the collector's reach for the rest of the session, once per step the user takes.
    /// </remarks>
    private void ReleaseReturnValue()
    {
        var captured = Interlocked.Exchange(ref _returnValue, null);
        if (captured?.Handle is { } handle)
        {
            try { handle.Dispose(); }
            catch { }
        }
    }

    /// <summary>
    /// The row for the value the step went over, or none when this frame did not make that call.
    /// </summary>
    /// <remarks>
    /// Offered on the stepping thread's leaf frame only. It belongs to the statement the step just
    /// finished, and showing it against a caller's frame — or against a frame in another thread the
    /// user happened to select — would attach it to a line that never made the call.
    /// </remarks>
    private DebugVariable? ReturnValueRow(CorDebugILFrame frame)
    {
        if (_returnValue is not { } captured || captured.ThreadId != ThreadOf(frame))
            return null;

        // The leaf, not merely the right thread: the value belongs to the statement the step
        // finished on, and a caller's frame is a line that never made the call.
        if (Safe(() => frame.Chain.Thread.ActiveFrame) is { } leaf && !leaf.Equals(frame))
            return null;

        var name = captured.Callee.Length > 0 ? $"{ReturnMarker} ({captured.Callee})" : ReturnMarker;
        if (captured.Handle is { } handle)
            return Row(name, handle, "return", ReturnMarker);

        return new DebugVariable
        {
            Name = name,
            Value = captured.Text,
            Kind = "return",
            Type = captured.Type,
            VariablesReference = string.Empty,
            Settable = false,
        };
    }

    /// <summary>Which thread a frame is on, by way of its chain — a frame does not name one.</summary>
    private static int ThreadOf(CorDebugFrame frame) =>
        Safe(() => (int?)ThreadId(frame.Chain.Thread)) ?? 0;
}
