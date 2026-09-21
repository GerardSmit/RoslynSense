using System.Runtime.CompilerServices;

namespace RoslynMCP.Lsp;

/// <summary>
/// Reference identity of a snapshot object, for a memo key that must recognise "the same
/// compilation as last time" without keeping that compilation alive.
/// </summary>
/// <remarks>
/// <para>
/// The code lens memo keys answers on a generation the pack describes, and three packs described
/// theirs with the Solution or Compilation the lenses were counted against. Equality by reference
/// is exactly right — a new snapshot is a new generation — but holding the reference was not: the
/// memo keeps the eight most recent files, so eight solution snapshots, each with every compilation
/// it had built, stayed resident until eight other files were code-lensed. On a large solution
/// that is gigabytes, and it was one of the roots found under a 14 GB daemon.
/// </para>
/// <para>
/// Two live wrappers of one object are equal; a wrapper whose object has been collected equals
/// nothing, including itself, so the next request re-keys and recomputes — which is right, because
/// a collected snapshot is one nothing serves any more. The hash is taken at construction so a
/// collected wrapper still hashes consistently.
/// </para>
/// </remarks>
internal sealed class WeakGeneration
{
    private readonly WeakReference<object> _target;
    private readonly int _hash;

    public WeakGeneration(object target)
    {
        _target = new WeakReference<object>(target);
        _hash = RuntimeHelpers.GetHashCode(target);
    }

    public bool IsAlive => _target.TryGetTarget(out _);

    public override bool Equals(object? obj) =>
        obj is WeakGeneration other
        && _target.TryGetTarget(out var mine)
        && other._target.TryGetTarget(out var theirs)
        && ReferenceEquals(mine, theirs);

    public override int GetHashCode() => _hash;
}
