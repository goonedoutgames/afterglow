namespace Afterglow.Core;

public static class AppPaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Afterglow");

    public static string LocalDbPath => Path.Combine(Root, "afterglow.db");
    public static string HubDataDir => Path.Combine(Root, "hub-data");
    public static string DefaultLibraryRoot => Path.Combine(Root, "library");
    public static string DownloadsTemp => Path.Combine(Root, "downloads-temp");
    public static string SidecarDir => Path.Combine(AppContext.BaseDirectory, "sidecar");

    public static string MediaCache => Path.Combine(Root, "media-cache");

    public static void EnsureDirectories(string? libraryRoot = null)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(HubDataDir);
        Directory.CreateDirectory(libraryRoot ?? DefaultLibraryRoot);
        Directory.CreateDirectory(DownloadsTemp);
        Directory.CreateDirectory(MediaCache);
    }
}
