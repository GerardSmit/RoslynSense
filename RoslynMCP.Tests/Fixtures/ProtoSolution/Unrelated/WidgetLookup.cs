namespace ProtoSolution.Unrelated;

/// <summary>A type that coincidentally spells the contract's message name and has nothing to do
/// with it: no ProjectReference reaches Contracts, so nothing here shares a symbol with the
/// generated code.</summary>
public class Widget
{
    public long Id { get; set; }

    public string Label { get; set; } = "";
}

/// <summary>Declares a method spelled exactly like the rpc. A search that scanned names instead of
/// following bound symbols would report this file, which is the failure this project exists to
/// make visible.</summary>
public class WidgetLookup
{
    public List<Widget> GetWidgetsById(IEnumerable<long> ids)
    {
        var widgets = new List<Widget>();

        foreach (var id in ids)
        {
            widgets.Add(new Widget { Id = id, Label = "local-" + id });
        }

        return widgets;
    }
}
