using System;
using System.Collections.Generic;

namespace SampleProject;

public delegate string ValueFormatter(int value);

public enum OutlineKind
{
    Basic,
    Advanced
}

public record OutlineRecord(int Value);

public class OutlineShowcase
{
    public const int DefaultValue = 10;
    private readonly List<int> _values = [];

    public static event EventHandler? StaticChanged;
    public event EventHandler? Changed;

    public string Name { get; init; } = string.Empty;

    public event EventHandler? Routed
    {
        add => StaticChanged += value;
        remove => StaticChanged -= value;
    }

    public int this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }

    public OutlineShowcase()
    {
    }

    ~OutlineShowcase()
    {
    }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public static OutlineShowcase operator +(OutlineShowcase left, OutlineShowcase right) => left;

    public static implicit operator int(OutlineShowcase value) => value._values.Count;

    public void MultilineParams(
        int firstParam,
        string secondParam,
        bool thirdParam)
    {
    }
}
