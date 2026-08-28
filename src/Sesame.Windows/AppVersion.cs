using System.Reflection;

namespace Sesame;

public static class AppVersion
{
    public static string Current =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Label => "v" + Current;
}
