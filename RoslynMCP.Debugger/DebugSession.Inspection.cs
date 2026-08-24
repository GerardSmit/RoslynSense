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

    /// <summary>Path segment prefix (<c>$more:N</c>) that continues an element listing from
    /// element N — the "..." row of a long array.</summary>
    internal const string MoreMarker = "$more";

    private DebugDisplayOptions _display = new();

    /// <summary>
    /// Which debugger attributes this session honours. Replaceable at any time, including while
    /// stopped, so a display string that looks wrong can be switched off and the raw fields read
    /// without restarting the target.
    /// </summary>
    public DebugDisplayOptions DisplayOptions
    {
        get => _display;
        set => _display = value ?? new DebugDisplayOptions();
    }

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

        // A "..." continuation names the same value with an element offset to resume from.
        var offset = 0;
        var basePath = path;
        var moreAt = path.LastIndexOf($".{MoreMarker}:", StringComparison.Ordinal);
        if (moreAt >= 0 && int.TryParse(path[(moreAt + MoreMarker.Length + 2)..], out var parsed))
        {
            offset = parsed;
            basePath = path[..moreAt];
        }

        var target = Safe(() => Dereference(value));
        if (target is null || target is CorDebugStringValue)
            return children;

        if (target is CorDebugArrayValue array)
        {
            AppendElements(children, array, basePath, offset);
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
                Value = "expanding this enumerates the IEnumerable",
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

        var viewType = FindTypeDef(value, "System.Linq.SystemCore_EnumerableDebugView");
        if (viewType is null)
        {
            error = "the enumerable view type was not found (System.Core is not loaded)";
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

        var view = RunEval(eval => eval.NewObject(ctor.Raw, 1, [value.Raw]), out error);
        return view is null ? null : MemberValue(view, "Items", callOnly: false, out error);
    }

    private DebugVariable RawViewRow(string path) => new()
    {
        Name = "Raw View",
        Value = "the object's own fields, unfiltered",
        Kind = "raw",
        Type = string.Empty,
        VariablesReference = $"{path}.{RawMarker}",
    };

    private void AppendElements(List<DebugVariable> into, CorDebugArrayValue array, string path, int from)
    {
        var count = Safe(() => (int?)array.Count) ?? 0;
        var rank = Safe(() => (int?)array.Rank) ?? 1;
        var dimensions = rank > 1 ? Safe(() => array.GetDimensions(rank)) : null;
        var end = Math.Min(count, from + Math.Max(1, _display.MaxChildren));

        for (var i = from; i < end; i++)
        {
            var element = Safe(() => array.GetElementAtPosition(i));
            if (element is null)
                continue;
            var name = dimensions is null
                ? $"[{i}]"
                : $"[{string.Join(",", IndicesOf(i, dimensions))}]";
            into.Add(Row(name, element, "element", $"{path}{name}"));
        }

        if (end < count)
            into.Add(new DebugVariable
            {
                Name = "...",
                Value = $"{count - end} more of {count}",
                Kind = "element",
                // Expanding the row continues the listing from where this page stopped.
                VariablesReference = $"{path}.{MoreMarker}:{end}",
            });
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

    // Deliberately not ICorDebugStepper2::SetJMC. The runtime's own Just My Code needs every user
    // module marked through SetJMCStatus first, and an unmarked process is one where *nothing* is
    // user code — so a JMC step never finds anywhere to stop and simply never completes, which is
    // exactly what it did here. Filtering the step completes ourselves needs no cooperation from
    // the runtime and behaves the same on an optimized module, which cannot be marked at all.

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
            if (declaringType is { } type && IsMarkedNonUser(metadata, type))
                return false;

            return true;
        }
        catch
        {
            // Unreadable frames are not somewhere to strand the user, so treat them as stoppable.
            return true;
        }
    }

    private static bool IsMarkedNonUser(MetaDataImport metadata, mdToken token) =>
        DebuggerAttributes.StepOverMarkers.Any(
            marker => Safe(() => DebuggerAttributes.Has(metadata, token, marker)) == true);

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
            stepper.StepOut();
            lock (_stepperLock)
                _steppers.Add(stepper);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly Lock _stepperLock = new();
}
