using System;
using System.IO;
using System.Threading;

namespace IntegrationFixture;

internal static class Program
{
    private static void Main()
    {
        var greeter = new Greeter();
        string output = Path.Combine(AppContext.BaseDirectory, "hotreload-values.txt");
        char input = Console.ReadKey(intercept: true).KeyChar;
        while (true)
        {
            File.AppendAllText(output, $"{Environment.ProcessId}|{input}|{greeter.Message}{Environment.NewLine}");
            Thread.Sleep(50);
        }
    }
}
