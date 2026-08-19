using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigApp;

/// <summary>Every configuration read shape the usage index recognizes, one per line.</summary>
public static class Program
{
    public static void Main()
    {
        IConfiguration config = null!;
        var services = new ServiceCollection();

        // Direct reads.
        var title = config["App:Title"];
        var retries = config.GetValue<int>("Example:Retries");
        var example = config.GetSection("Example");
        var nestedName = config.GetSection("Example").GetSection("Nested")["Name"];
        var connection = config.GetConnectionString("Main");
        var secret = config["Integration:ApiKey"];

        // Read but never declared, inside a section that is declared: what completion offers
        // when the caret is inside App.
        var region = config["App:Region"];

        // Binding, in each of its spellings.
        services.Configure<ExampleOptions>(config.GetSection("Example"));
        services.AddOptions<ExampleOptions>().BindConfiguration("Example");

        var bound = new ExampleOptions();
        example.Bind(bound);

        var snapshot = config.GetSection("Example").Get<ExampleOptions>();
    }
}

/// <summary>A consumer of the bound options: the reference a bound key's lens counts.</summary>
public class Consumer
{
    public int Read(ExampleOptions options) => options.Retries;
}
