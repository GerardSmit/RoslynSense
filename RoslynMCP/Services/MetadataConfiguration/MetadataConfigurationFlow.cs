using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RoslynMCP.Services.MetadataConfiguration;

/// <summary>
/// Follows Framework configuration collections through locals and same-assembly helpers.
/// Only methods reached from an AppSettings/ConnectionStrings getter are interpreted. Collection
/// provenance describes a possible configuration value, including across exception handlers;
/// arbitrary unknown return values never become configuration values. No code is executed.
/// </summary>
internal sealed class MetadataConfigurationFlow(PEReader pe, MetadataReader md, MetadataConstantStrings constants)
{
    private enum Kind { Unknown, Literal, Collection, Keys, Enumerator, Key, Conflict }
    private readonly record struct Value(Kind Kind, string? Text = null, string? Owner = null);
    private readonly record struct Instruction(OpCode Op, int Operand, int Next, int[] Targets);
    private readonly Dictionary<int, Dictionary<int, Instruction>> _instructions = [];
    private readonly Queue<(int Token, Value[] Arguments)> _methods = new();
    private readonly HashSet<string> _visited = [];
    private readonly HashSet<MetadataConfigurationCandidate> _reads = [];
    private readonly Dictionary<int, HashSet<int>> _callers = [];
    private readonly Dictionary<int, Value> _returns = [];
    private int _budget = 100_000;
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static IEnumerable<MetadataConfigurationCandidate> Scan(
        PEReader pe, MetadataReader md, MetadataConstantStrings constants)
    {
        var flow = new MetadataConfigurationFlow(pe, md, constants);
        var getters = new HashSet<int>();
        foreach (var h in md.MemberReferences)
            if (IsCollection(md.GetString(md.GetMemberReference(h).Name))) getters.Add(MetadataTokens.GetToken(h));
        foreach (var h in md.MethodDefinitions)
            if (IsCollection(md.GetString(md.GetMethodDefinition(h).Name))) getters.Add(MetadataTokens.GetToken(h));
        if (getters.Count == 0) return [];

        foreach (var h in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(h);
            if (method.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
            // Cheap gate only. The interpreter subsequently decodes real instructions.
            int token = MetadataTokens.GetToken(h);
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] is 0x28 or 0x6f)
                {
                    int called = BitConverter.ToInt32(il, i + 1);
                    if (getters.Contains(called)) flow.Enqueue(token, []);
                    if ((called & unchecked((int)0xff000000)) == 0x06000000)
                    {
                        if (!flow._callers.TryGetValue(called, out var callers))
                            flow._callers[called] = callers = [];
                        callers.Add(token);
                    }
                }
        }
        while (flow._methods.TryDequeue(out var method) && flow._budget > 0)
            flow.Analyze(method.Token, method.Arguments);
        return flow._reads;
    }

    private static bool IsCollection(string? name) => name is "get_AppSettings" or "get_ConnectionStrings";

    private void Enqueue(int token, Value[] arguments, bool revisit = false)
    {
        if (_visited.Count >= 512 || MetadataTokens.EntityHandle(token).Kind != HandleKind.MethodDefinition) return;
        // Literal arguments are not propagated: only configuration provenance needs another method.
        var relevant = arguments.Select(v => v.Kind is Kind.Collection or Kind.Keys or Kind.Enumerator or Kind.Key ? v : default).ToArray();
        string key = token + ":" + string.Join("|", relevant.Select(v => v.ToString()));
        if (_visited.Add(key) || revisit) _methods.Enqueue((token, relevant));
    }

    private static bool IsProvenance(Value value) =>
        value.Kind is Kind.Collection or Kind.Keys or Kind.Enumerator or Kind.Key;

    private static Value Join(Value value, Value incoming)
    {
        if (value == incoming) return value;
        if (value.Kind == Kind.Conflict || incoming.Kind == Kind.Conflict) return new(Kind.Conflict);
        // A path yielding null/unknown does not erase a possible configuration collection.
        // Literal keys still need agreement: merging unrelated literal values is not safe.
        if (value == default && IsProvenance(incoming)) return incoming;
        if (incoming == default && IsProvenance(value)) return value;
        return new(Kind.Conflict);
    }

    private sealed class State
    {
        public List<Value> Stack = [];
        public Dictionary<int, Value> Locals = [];
        public State Copy() => new() { Stack = [.. Stack], Locals = new(Locals) };
        public Value Pop()
        {
            if (Stack.Count == 0) return default;
            var v = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); return v;
        }
        public bool Merge(State incoming)
        {
            bool changed = false;
            if (Stack.Count != incoming.Stack.Count)
            {
                if (Stack.Count > 0) { Stack.Clear(); changed = true; }
            }
            else for (int i = 0; i < Stack.Count; i++)
            {
                var merged = Join(Stack[i], incoming.Stack[i]);
                if (Stack[i] != merged) { Stack[i] = merged; changed = true; }
            }
            foreach (int local in Locals.Keys.Union(incoming.Locals.Keys).ToArray())
            {
                var previous = Locals.GetValueOrDefault(local);
                var merged = Join(previous, incoming.Locals.GetValueOrDefault(local));
                if (previous != merged) { Locals[local] = merged; changed = true; }
            }
            return changed;
        }
    }

    private void Analyze(int token, Value[] arguments)
    {
        var method = md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token));
        if (method.RelativeVirtualAddress == 0) return;
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (!_instructions.TryGetValue(token, out var code))
            _instructions[token] = code = Decode(body.GetILBytes() ?? []);
        if (code.Count == 0) return;
        string owner = MetadataConfigurationScanner.TypeName(md, method.GetDeclaringType());
        string name = md.GetString(method.Name);
        var states = new Dictionary<int, State>();
        var queue = new Queue<int>();
        var finallyWrites = new Dictionary<int, HashSet<int>>();
        void Offer(int offset, State state)
        {
            if (!code.ContainsKey(offset)) return;
            if (!states.TryGetValue(offset, out var previous)) { states[offset] = state.Copy(); queue.Enqueue(offset); }
            else if (previous.Merge(state)) queue.Enqueue(offset);
        }
        Offer(0, new State());
        foreach (var region in body.ExceptionRegions)
        {
            var state = new State();
            if (region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter) state.Stack.Add(default);
            Offer(region.HandlerOffset, state);
            if (region.Kind == ExceptionRegionKind.Filter) Offer(region.FilterOffset, state);
        }
        while (queue.TryDequeue(out int offset) && --_budget > 0)
        {
            var state = states[offset].Copy();
            var instruction = code[offset];
            short op = instruction.Op.Value;
            // Exceptions and leave transfer locals into the handler, with a fresh evaluation
            // stack. In particular, a collection assigned in a try remains visible in finally.
            foreach (var region in body.ExceptionRegions)
                if (offset >= region.TryOffset && offset < region.TryOffset + region.TryLength)
                {
                    var handler = state.Copy();
                    handler.Stack.Clear();
                    if (region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter)
                        handler.Stack.Add(default);
                    Offer(region.HandlerOffset, handler);
                    if (region.Kind == ExceptionRegionKind.Filter) Offer(region.FilterOffset, handler);
                }
            if (op == OpCodes.Ret.Value && arguments.All(v => !IsProvenance(v)) &&
                state.Stack.Count == 1 && state.Stack[0].Kind == Kind.Collection &&
                _returns.TryAdd(token, state.Stack[0]) && _callers.TryGetValue(token, out var callers))
                foreach (int caller in callers) Enqueue(caller, [], revisit: true);
            if (op == OpCodes.Ldstr.Value)
                state.Stack.Add(new(Kind.Literal, md.GetUserString(MetadataTokens.UserStringHandle(instruction.Operand))));
            else if (op == OpCodes.Dup.Value) { var v = state.Pop(); state.Stack.Add(v); state.Stack.Add(v); }
            else if (Slot(op, instruction.Operand, argument: true, store: false) is { } argument)
                state.Stack.Add(argument < arguments.Length ? arguments[argument] : default);
            else if (Slot(op, instruction.Operand, argument: false, store: false) is { } local)
                state.Stack.Add(state.Locals.GetValueOrDefault(local));
            else if (Slot(op, instruction.Operand, argument: false, store: true) is { } destination)
            {
                var value = state.Pop();
                if (value == default) state.Locals.Remove(destination); else state.Locals[destination] = value;
            }
            else if (op == OpCodes.Castclass.Value || op == OpCodes.Isinst.Value) { /* provenance survives a cast */ }
            else if (op is 0x28 or 0x6f or 0x73)
                Call(instruction.Operand, op == 0x73, state, owner, name);
            else
            {
                int pops = Count(instruction.Op.StackBehaviourPop);
                int pushes = Count(instruction.Op.StackBehaviourPush);
                if (pops < 0 || pushes < 0) state.Stack.Clear();
                else { for (int i = 0; i < pops; i++) state.Pop(); for (int i = 0; i < pushes; i++) state.Stack.Add(default); }
                // Address-taking/mutation prevents attributing later reads to stale local values.
                if (op is 0x12 or unchecked((short)0xfe0d)) state.Locals.Remove(instruction.Operand);
            }
            if (state.Stack.Count > 64) continue;
            if (op == OpCodes.Leave.Value || op == OpCodes.Leave_S.Value)
            {
                state.Stack.Clear();
                // The direct leave edge skips execution of finally. Handler reads were offered
                // the original locals above; its continuation must not reuse values that the
                // finally can overwrite (including ref/out mutation through a local address).
                foreach (var region in body.ExceptionRegions)
                    if (region.Kind == ExceptionRegionKind.Finally &&
                        offset >= region.TryOffset && offset < region.TryOffset + region.TryLength &&
                        instruction.Targets.Any(t => t < region.TryOffset || t >= region.TryOffset + region.TryLength))
                    {
                        if (!finallyWrites.TryGetValue(region.HandlerOffset, out var writes))
                        {
                            finallyWrites[region.HandlerOffset] = writes = [];
                            foreach (var (handlerOffset, handlerInstruction) in code)
                                if (handlerOffset >= region.HandlerOffset &&
                                    handlerOffset < region.HandlerOffset + region.HandlerLength)
                                {
                                    short handlerOp = handlerInstruction.Op.Value;
                                    if (Slot(handlerOp, handlerInstruction.Operand, argument: false, store: true) is { } written)
                                        writes.Add(written);
                                    else if (handlerOp is 0x12 or unchecked((short)0xfe0d))
                                        writes.Add(handlerInstruction.Operand);
                                }
                        }
                        foreach (int written in writes) state.Locals.Remove(written);
                    }
            }
            foreach (int target in instruction.Targets) Offer(target, state);
            if (instruction.Op.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw))
                Offer(instruction.Next, state);
        }
    }

    private void Call(int token, bool constructor, State state, string owner, string method)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
            handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        BlobHandle signature = handle.Kind switch
        {
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)handle).Signature,
            _ => default
        };
        if (signature.IsNil) { state.Stack.Clear(); return; }
        var reader = md.GetBlobReader(signature);
        var header = reader.ReadSignatureHeader();
        if (header.IsGeneric) reader.ReadCompressedInteger();
        int count = reader.ReadCompressedInteger();
        if (count > 64) { state.Stack.Clear(); return; }
        var returnType = reader.ReadSignatureTypeCode();
        var args = new Value[count];
        for (int i = count - 1; i >= 0; i--) args[i] = state.Pop();
        var receiver = header.IsInstance && !constructor ? state.Pop() : default;
        var (name, type) = MetadataConfigurationScanner.Member(md, token);
        Value result = default;
        if (!header.IsInstance && IsCollection(name)) result = new(Kind.Collection, name, type);
        else if (constants.Resolve(token) is { } literal) result = new(Kind.Literal, literal);
        else if (handle.Kind == HandleKind.MethodDefinition &&
                 _returns.TryGetValue(MetadataTokens.GetToken(handle), out var returned)) result = returned;
        else if (receiver.Kind == Kind.Collection && name == "get_Keys") result = receiver with { Kind = Kind.Keys };
        else if (receiver.Kind == Kind.Keys && name == "GetEnumerator") result = receiver with { Kind = Kind.Enumerator };
        else if (receiver.Kind == Kind.Enumerator && name == "get_Current") result = receiver with { Kind = Kind.Key };
        else if (receiver.Kind == Kind.Collection && name is "get_Item" or "Get" && args.Length == 1 && args[0].Kind == Kind.Literal)
            Record(args[0].Text!, receiver, owner, method, prefix: false);
        else if (receiver.Kind == Kind.Key && type == "System.String" && name == "StartsWith"
                 && args.Length == 1 && args[0].Kind == Kind.Literal)
            Record(args[0].Text!, receiver, owner, method, prefix: true);

        if (handle.Kind == HandleKind.MethodDefinition
            && args.Append(receiver).Any(v => v.Kind is Kind.Collection or Kind.Keys or Kind.Enumerator or Kind.Key))
            Enqueue(MetadataTokens.GetToken(handle), header.IsInstance ? [receiver, .. args] : args);
        if (constructor || returnType != SignatureTypeCode.Void) state.Stack.Add(result);
    }

    private void Record(string literal, Value source, string owner, string method, bool prefix)
    {
        if (literal.Length > 0)
            _reads.Add(new(literal, "get_Item", "System.Collections.Specialized.NameValueCollection",
                source.Text, source.Owner, owner, method, prefix));
    }

    private static int? Slot(short opcode, int operand, bool argument, bool store)
    {
        if (argument) return opcode switch { >= 0x02 and <= 0x05 => opcode - 0x02, 0x0e or unchecked((short)0xfe09) => operand, _ => null };
        if (store) return opcode switch { >= 0x0a and <= 0x0d => opcode - 0x0a, 0x13 or unchecked((short)0xfe0e) => operand, _ => null };
        return opcode switch { >= 0x06 and <= 0x09 => opcode - 0x06, 0x11 or unchecked((short)0xfe0c) => operand, _ => null };
    }

    private static int Count(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref or StackBehaviour.Push1
            or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi
            or StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_pop1 or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref => 3,
        _ => -1
    };

    private static Dictionary<int, Instruction> Decode(byte[] il)
    {
        var result = new Dictionary<int, Instruction>();
        for (int p = 0; p < il.Length;)
        {
            int start = p;
            short value = il[p++];
            if (value == 0xfe) { if (p == il.Length) return []; value = (short)(0xfe00 | il[p++]); }
            if (!Opcodes.TryGetValue(value, out var op)) return [];
            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => p + 4 <= il.Length ? 4 + BitConverter.ToInt32(il, p) * 4 : -1,
                _ => 4
            };
            if (size < 0 || size > il.Length - p) return [];
            int operand = size == 1 ? il[p] : size == 2 ? BitConverter.ToUInt16(il, p) : size >= 4 ? BitConverter.ToInt32(il, p) : 0;
            int next = p + size;
            int[] targets = op.OperandType switch
            {
                OperandType.ShortInlineBrTarget => [next + (sbyte)operand],
                OperandType.InlineBrTarget => [next + operand],
                OperandType.InlineSwitch => Enumerable.Range(0, (size - 4) / 4).Select(i => next + BitConverter.ToInt32(il, p + 4 + i * 4)).ToArray(),
                _ => []
            };
            result[start] = new(op, operand, next, targets);
            p = next;
        }
        return result;
    }
}
