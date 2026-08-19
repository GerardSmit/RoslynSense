namespace Alpha;

/// <summary>One unused local, and so one CS0168 the Directory.Build.props is hiding.</summary>
public static class Counter
{
    public static int Count()
    {
        int unused;
        return 1;
    }
}
