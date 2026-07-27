namespace RoslynMCP.Tools;

/// <summary>
/// Marks a tool type whose calls must never be forwarded to the shared host.
/// </summary>
/// <remarks>
/// Such tools own a stateful, process-wide session — a debugger attached to a target, or a
/// launched application and its child process tree. The shared host is shared across chats, so
/// forwarding these would make two chats fight over one session. Marked tools run in-process in
/// each client instead, giving every chat its own independent session. Heavy read/analysis tools
/// stay forwarded so the solution is still loaded only once.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class InProcessOnlyAttribute : Attribute;
