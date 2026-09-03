namespace ConfigApp;

/// <summary>The options class the fixture binds to the <c>Example</c> section.</summary>
public class ExampleOptions
{
    public int Retries { get; set; }

    public bool Enabled { get; set; }

    public LogMode Mode { get; set; }

    public NestedOptions Nested { get; set; } = new();
}

public class NestedOptions
{
    public string? Name { get; set; }
}

public enum LogMode
{
    Quiet,
    Verbose,
}
