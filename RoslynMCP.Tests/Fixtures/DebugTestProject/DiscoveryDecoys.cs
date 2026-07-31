// Deliberately its own namespace: declaring FactAttribute in DebugTestProject would shadow
// Xunit's for every file in that namespace and silently unregister the real tests.
namespace DebugTestProject.Decoys;

/// <summary>
/// Methods that look like tests to a name-only matcher but are not tests. Discovery resolves
/// attributes semantically, so none of these may be reported.
/// </summary>
public class DiscoveryDecoys
{
    /// <summary>A method merely named after a test attribute.</summary>
    public void Fact()
    {
    }

    /// <summary>A same-named attribute from an unrelated namespace.</summary>
    [Fact]
    public void NotATestDespiteTheAttributeName()
    {
    }

    [Test]
    public void AlsoNotATest()
    {
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;
