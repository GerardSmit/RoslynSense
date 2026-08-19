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
        var target = Safe(() => Dereference(value));
        if (target is null || target is CorDebugStringValue)
            return children;

        if (target is CorDebugArrayValue array)
        {
            AppendElements(children, array, path);
            return children;
        }

        if (target is not CorDebugObjectValue)
            return children;

        // A "Raw View" expansion asks for exactly what is in memory, so neither the proxy nor the
        // browsable states apply to it.
        var raw = path.EndsWith("." + RawMarker, StringComparison.Ordinal);

        if (!raw && _display.TypeProxy && ProxyTypeNameOf(value) is not null)
        {
            AppendProxyMembers(children, value, path);
            if (_display.RawView)
                children.Add(RawViewRow(path));
            return children;
        }

        var hidNothing = AppendFields(children, value, path, applyBrowsable: !raw && _display.Browsable);
        if (!raw && !hidNothing && _display.RawView)
            children.Add(RawViewRow(path));

        return children;
    }

    private DebugVariable RawViewRow(string path) => new()
    {
        Name = "Raw View",
        Value = "the object's own fields, unfiltered",
        Kind = "raw",
        Type = string.Empty,
        VariablesReference = $"{path}.{RawMarker}",
    };

    private void AppendElements(List<DebugVariable> into, CorDebugArrayValue array, string path)
    {
        var count = Safe(() => (int?)array.Count) ?? 0;
        var shown = Math.Min(count, Math.Max(1, _display.MaxChildren));

        for (var i = 0; i < shown; i++)
        {
            var element = Safe(() => array.GetElementAtPosition(i));
            if (element is null)
                continue;
            into.Add(Row($"[{i}]", element, "element", $"{path}[{i}]"));
        }

        if (shown < count)
            into.Add(new DebugVariable
            {
                Name = "...",
                Value = $"{count - shown} more of {count}",
                Kind = "element",
            });
    }

    /// <summary>
    /// Lists an object's instance fields, applying <c>DebuggerBrowsable</c>.
    /// </summary>
    /// <returns>Whether every field was listed — false when something was hidden or inlined, which
    /// is what decides whether a Raw View node is worth offering.</returns>
    private bool AppendFields(List<DebugVariable> into, CorDebugValue value, string path, bool applyBrowsable)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            return true;

        var complete = true;
        var budget = Math.Max(1, _display.MaxChildren);

        foreach (var (cls, metadata, typeDef) in TypeChain(value))
        {
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

                var state = applyBrowsable
                    ? DebuggerAttributes.BrowsableOf(metadata, field)
                    : BrowsableState.Collapsed;
                if (state == BrowsableState.Never)
                {
                    complete = false;
                    continue;
                }

                var name = DisplayFieldName(props.Value.szField);
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

                into.Add(Row(name, member, "field", $"{path}.{name}"));
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

    /// <summary>A frame's arguments and locals, as expandable rows.</summary>
    private List<DebugVariable> FrameVariables(CorDebugILFrame ilFrame)
    {
        var variables = new List<DebugVariable>();
        var (argNames, localNames) = FrameSymbolNames(ilFrame);
        AppendValues(variables, "arg", Safe(() => ilFrame.Arguments), argNames);
        AppendValues(variables, "local", Safe(() => ilFrame.LocalVariables), localNames);
        return variables;
    }

    private DebugVariable Row(string name, CorDebugValue value, string kind, string path) => new()
    {
        Name = name,
        Value = DescribeValue(value),
        Kind = kind,
        Type = TypeNameOf(value),
        VariablesReference = Expandable(value) ? path : string.Empty,
        Settable = kind is "field" or "element",
    };

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

        var format = FirstAttributeInChain(value, DebuggerAttributes.Display);
        if (format is null || format.Length == 0)
            return null;

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
                current = ElementValue(current, index);
                if (current is null)
                {
                    error = $"cannot index into '{name}'";
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
    /// Bounded to types declared in the same module: crossing to a base type in another assembly
    /// means resolving a TypeRef through its AssemblyRef, and everything read here — fields,
    /// browsable states, display strings — is about the debuggee's own types.
    /// </remarks>
    private static IEnumerable<(CorDebugClass Class, MetaDataImport Metadata, mdTypeDef TypeDef)> TypeChain(
        CorDebugValue value)
    {
        var target = Safe(() => Dereference(value));
        if (target is not CorDebugObjectValue obj)
            yield break;

        var cls = Safe(() => obj.Class);
        for (var depth = 0; cls is not null && depth < MaxTypeDepth; depth++)
        {
            var module = Safe(() => cls.Module);
            if (module is null)
                yield break;
            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null)
                yield break;

            yield return (cls, metadata, cls.Token);

            var current = cls;
            cls = Safe(() =>
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

        foreach (var (_, metadata, typeDef) in TypeChain(value))
        {
            var name = Safe(() => metadata.GetTypeDefProps(typeDef).szTypeDef);
            if (name is { Length: > 0 })
                return name;
        }
        return Safe(() => (target ?? value).Type.ToString()) ?? string.Empty;
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
