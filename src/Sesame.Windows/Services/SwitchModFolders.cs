namespace Sesame.Services;

public static class SwitchModFolders
{
    public static bool IsDisabled(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith("disabled", StringComparison.OrdinalIgnoreCase);

    public static string BaseName(string name)
    {
        if (!IsDisabled(name)) return name;
        var rest = name[8..].TrimStart(' ', '_', '-');
        return string.IsNullOrWhiteSpace(rest) ? name : rest;
    }

    public static string DisabledName(string name) =>
        IsDisabled(name) ? name : "disabled " + BaseName(name);

    public static string EnabledName(string name) => BaseName(name);

    public static string Sibling(string remotePath, string newFolderName)
    {
        var trimmed = remotePath.TrimEnd('/');
        return DeckClient.Combine(DeckClient.Parent(trimmed), newFolderName);
    }
}
