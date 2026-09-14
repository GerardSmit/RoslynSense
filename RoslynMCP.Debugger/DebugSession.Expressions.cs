using System.Reflection;
using ClrDebug;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Debugger;

public sealed partial class DebugSession
{
    private bool AssignExpression(CorDebugILFrame frame, string expression, string replacement, out string error)
    {
        error = string.Empty;
        var syntax = SyntaxFactory.ParseExpression(expression);
        if (syntax is MemberAccessExpressionSyntax member)
        {
            var parent = ResolvePath(frame, member.Expression.ToString(), out error);
            if (parent is null) return false;
            var propertyName = member.Name.Identifier.ValueText;
            if (FindMethod(parent, "set_" + propertyName, parameterCount: 1) is { } setter)
            {
                if (Dereference(parent).ExactType.Type != CorElementType.ValueType)
                {
                    EvaluateCompiledExpression(frame, expression + " = (" + replacement + ")", out error);
                    return error.Length == 0;
                }
                var current = MemberValue(parent, propertyName, false, out error);
                if (current is null) return false;
                var argument = EvaluateCompiledExpression(frame, "(" + TypeNameOf(current).Replace('+', '.') + ")(" + replacement + ")", out error);
                if (argument is null) return false;
                parent = ResolvePath(frame, member.Expression.ToString(), out error);
                if (parent is null) return false;
                var argumentValue = Dereference(argument);
                InvokeFunction(setter.Function, [parent,
                    TryReadScalar(argumentValue) is not null || argumentValue is CorDebugObjectValue && argumentValue.Type == CorElementType.ValueType
                        ? argumentValue : argument], out error);
                return error.Length == 0;
            }
            if (FindMethod(parent, "get_" + propertyName) is not null)
            {
                error = "The property has no setter.";
                return false;
            }
        }
        var target = ResolvePath(frame, expression, out error);
        if (target is null) return false;
        if (TryWriteScalar(target, replacement, out _)) return true;
        var type = TypeNameOf(target).Replace('+', '.');
        var source = EvaluateCompiledExpression(frame, "(" + type + ")(" + replacement + ")", out error);
        if (source is null) return false;
        try
        {
            // Function evaluation may invalidate stack value wrappers; read the destination again.
            target = ResolvePath(frame, expression, out error);
            if (target is null) return false;
            if (target is CorDebugReferenceValue reference && source is CorDebugReferenceValue sourceReference)
            {
                reference.Value = sourceReference.IsNull ? 0 : sourceReference.Value;
                return true;
            }
            var unboxed = Dereference(source);
            if (TryReadScalar(unboxed) is { } scalar) return TryWriteScalar(target, scalar, out error);
            var destination = Extensions.As<CorDebugGenericValue>(target);
            var value = Extensions.As<CorDebugGenericValue>(unboxed);
            if (destination.Size != value.Size) throw new InvalidOperationException("The value has the wrong size for this variable.");
            var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(value.Size);
            try { value.GetValue(memory); destination.SetValue(memory); }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private sealed record ExpressionArgument(string Name, string Type, string Parameter);
    private readonly Dictionary<string, (string Type, ExpressionArgument[] Arguments)> _compiledExpressions = new();
    private string? _expressionDirectory;

    /// <summary>Compile against the target's assemblies, then execute on the inspected thread.
    /// Simple member reads stay on the direct path; C# owns overload resolution and operators.</summary>
    private CorDebugValue? EvaluateCompiledExpression(CorDebugILFrame frame, string expression, out string error)
    {
        error = string.Empty;
        try
        {
            var syntax = SyntaxFactory.ParseExpression(expression);
            if (syntax.ContainsDiagnostics)
            {
                error = string.Join("; ", syntax.GetDiagnostics().Select(d => d.GetMessage()));
                return null;
            }
            var (args, locals) = FrameSymbolNames(frame);
            CorDebugValue? ResolveArgument(string name)
            {
                var current = _inspectionThread is { } thread && FrameAt(thread, _inspectionFrameIndex) is CorDebugILFrame refreshed ? refreshed : frame;
                return name == "this" ? RootValue(current, name, args, locals)
                    : RootValue(current, name, args, locals) ?? CapturedValue(current, name, locals)
                      ?? (RootValue(current, "this", args, locals) is { } self ? FieldValue(self, name) : null)
                      ?? FrameStaticValue(current, name);
            }


            var names = syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(n => n.Parent is not MemberAccessExpressionSyntax member || member.Name != n)
                .Where(n => !IsLambdaParameter(n))
                .Select(n => n.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            if (syntax.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any()) names.Add("this");
            var arguments = new List<ExpressionArgument>();
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                if (ResolveArgument(name) is not { } value) continue;
                var type = TypeNameOf(value).Replace('+', '.');
                if (SyntaxFactory.ParseTypeName(type).ContainsDiagnostics)
                    throw new InvalidOperationException($"The runtime type of '{name}' cannot be named in C#: {type}.");
                arguments.Add(new(name, type, "__debugArg" + arguments.Count));
            }
            var key = expression + "\n" + string.Join(";", arguments.Select(a => a.Name + ":" + a.Type));
            if (!_compiledExpressions.TryGetValue(key, out var compiled))
            {
                if (_compiledExpressions.Count >= 256)
                    throw new InvalidOperationException("This session has compiled 256 distinct expressions. Restart debugging to compile more.");
                var references = new List<MetadataReference>();
                var assemblyNames = new HashSet<string>(StringComparer.Ordinal);
                var paths = LoadedModules().Select(m => Safe(() => m.Name)).OfType<string>().Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (_runtime == DebugRuntime.NetFramework)
                {
                    var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET",
                        Environment.Is64BitProcess ? "Framework64" : "Framework", "v4.0.30319");
                    foreach (var name in new[] { "System.Core.dll", "System.dll" })
                        if (File.Exists(Path.Combine(framework, name))) paths.Add(Path.Combine(framework, name));
                }
                // LINQ can be used before the debuggee itself has loaded its implementation.
                foreach (var directory in paths.Select(Path.GetDirectoryName).OfType<string>().Distinct().ToArray())
                    foreach (var name in new[] { "System.Core.dll", "System.Linq.dll", "System.Runtime.dll", "netstandard.dll" })
                        if (File.Exists(Path.Combine(directory, name))) paths.Add(Path.Combine(directory, name));
                foreach (var path in paths)
                {
                    try
                    {
                        var name = AssemblyName.GetAssemblyName(path).Name!;
                        if (!assemblyNames.Add(name)) continue;
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch (BadImageFormatException) { }
                }
                var typeName = "DebuggerExpression_" + Guid.NewGuid().ToString("N");
                var rewritten = new ArgumentRewriter(arguments.ToDictionary(a => a.Name, a => a.Parameter)).Visit(syntax)!;
                var attributes = string.Join("\n", assemblyNames.Select(n =>
                    "[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(" + SymbolDisplay.FormatLiteral(n, true) + ")]"));
                var source = "using System; using System.Linq; using System.Collections.Generic;\n" + attributes + "\n" +
                    "namespace System.Runtime.CompilerServices { [AttributeUsage(AttributeTargets.Assembly, AllowMultiple=true)] " +
                    "public sealed class IgnoresAccessChecksToAttribute : Attribute { public IgnoresAccessChecksToAttribute(string name) {} } }\n" +
                    "internal static class " + typeName + " { private static object Evaluate(" +
                    string.Join(",", arguments.Select(a => a.Type + " " + a.Parameter)) + ") { return (object)(" + rewritten.ToFullString() + "); } }";
                var compilation = CSharpCompilation.Create(typeName, [CSharpSyntaxTree.ParseText(source)], references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release)
                        .WithTopLevelBinderFlags(BinderFlags.IgnoreAccessibility));
                using var image = new MemoryStream();
                var emit = compilation.Emit(image);
                if (!emit.Success)
                {
                    error = string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage()));
                    return null;
                }
                _expressionDirectory ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "roslynsense-expressions-" + Guid.NewGuid().ToString("N"))).FullName;
                var pathToLoad = Path.Combine(_expressionDirectory, typeName + ".dll");
                File.WriteAllBytes(pathToLoad, image.ToArray());
                if (CallStatic("System.Reflection.Assembly", "LoadFrom", [pathToLoad], out error) is null) return null;
                compiled = (typeName, arguments.ToArray());
                _compiledExpressions[key] = compiled;
            }
            var function = FindStatic(compiled.Type, "Evaluate", compiled.Arguments.Length)
                ?? throw new InvalidOperationException("The compiled expression could not be located in the target.");
            // Loading the helper resumes the target; reacquire arguments after the load.
            var values = compiled.Arguments.Select(a => ResolveArgument(a.Name)
                ?? throw new InvalidOperationException($"'{a.Name}' is no longer available in the frame.")).ToArray();
            return InvokeFunction(function, values, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool IsLambdaParameter(IdentifierNameSyntax identifier)
    {
        var name = identifier.Identifier.ValueText;
        return identifier.Ancestors().OfType<LambdaExpressionSyntax>().Any(lambda => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText == name,
            ParenthesizedLambdaExpressionSyntax list => list.ParameterList.Parameters.Any(p => p.Identifier.ValueText == name),
            _ => false,
        });
    }

    private sealed class ArgumentRewriter(Dictionary<string, string> names) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            (node.Parent is not MemberAccessExpressionSyntax member || member.Name != node) && !IsLambdaParameter(node)
            && names.TryGetValue(node.Identifier.ValueText, out var parameter)
                ? SyntaxFactory.IdentifierName(parameter).WithTriviaFrom(node) : base.VisitIdentifierName(node);
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node) => names.TryGetValue("this", out var parameter)
            ? SyntaxFactory.IdentifierName(parameter).WithTriviaFrom(node) : base.VisitThisExpression(node);
    }
}
