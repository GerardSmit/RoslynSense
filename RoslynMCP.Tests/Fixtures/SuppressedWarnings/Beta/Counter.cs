namespace Beta;

/// <summary>Two, so a count across both projects cannot be mistaken for a count of either.</summary>
public static class Counter
{
    public static int Count()
    {
        int unused;
        int alsoUnused;
        return 2;
    }
}
