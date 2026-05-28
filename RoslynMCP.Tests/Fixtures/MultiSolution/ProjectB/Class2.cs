using ProjectA;

namespace ProjectB;

public static class Caller
{
    public static string Run() => Greeter.Hello("ProjectB");
}
