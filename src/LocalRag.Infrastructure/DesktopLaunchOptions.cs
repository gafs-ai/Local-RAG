namespace LocalRag.Infrastructure;

public sealed record DesktopLaunchOptions(string DataDirectory, bool StartInTray)
{
    public static DesktopLaunchOptions? Parse(IReadOnlyList<string> args, string defaultDirectory)
    {
        string? customDirectory = null;
        var tray = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--startup-tray" when !tray:
                    tray = true;
                    break;
                case "--data-dir" when customDirectory is null && i + 1 < args.Count:
                    customDirectory = args[++i];
                    if (string.IsNullOrWhiteSpace(customDirectory) || customDirectory.StartsWith("--", StringComparison.Ordinal))
                        return null;
                    break;
                default:
                    return null;
            }
        }
        try { return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(customDirectory ?? defaultDirectory)), tray); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
