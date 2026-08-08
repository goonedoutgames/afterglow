using Avalonia.Media;

namespace Afterglow.Services;

/// <summary>Platform logos (vector paths) and hoster favicon URLs.</summary>
public static class BrandIcons
{
    // Simplified brand marks (viewBox ~0..24) for Path.Data
    public const string WindowsPath =
        "M1,1 H11 V11 H1 Z M13,1 H23 V11 H13 Z M1,13 H11 V23 H1 Z M13,13 H23 V23 H13 Z";
    public const string LinuxPath =
        "M12,2 C8,2 6,6 6,9 C6,11 7,12 7,14 C5,14 3,15 3,18 C3,21 6,22 9,22 C10.5,22 11,21 12,21 C13,21 13.5,22 15,22 C18,22 21,21 21,18 C21,15 19,14 17,14 C17,12 18,11 18,9 C18,6 16,2 12,2 Z M9,10 A1,1 0 1,0 9,12 A1,1 0 1,0 9,10 Z M15,10 A1,1 0 1,0 15,12 A1,1 0 1,0 15,10 Z";
    public const string MacPath =
        "M18.7,12.4 C18.7,9.9 20.7,8.6 20.8,8.5 C19.6,6.8 17.7,6.5 17,6.5 C15.3,6.3 13.7,7.5 12.8,7.5 C11.9,7.5 10.5,6.5 9.1,6.6 C7.2,6.6 5.5,7.7 4.5,9.5 C2.5,13 4,18.2 5.9,21 C6.9,22.3 8,23.9 9.5,23.8 C10.9,23.8 11.5,22.9 13.2,22.9 C14.9,22.9 15.4,23.8 16.9,23.8 C18.4,23.7 19.4,22.4 20.4,21 C21.5,19.4 22,17.9 22,17.8 C22,17.7 18.7,16.4 18.7,12.4 Z M15.9,4.7 C16.7,3.7 17.2,2.3 17,1 C15.8,1.1 14.4,1.8 13.5,2.8 C12.8,3.7 12.2,5.1 12.4,6.4 C13.7,6.5 15.1,5.7 15.9,4.7 Z";
    public const string AndroidPath =
        "M17.6,9.5 L19.2,6.7 C19.4,6.4 19.3,6 19,5.8 C18.7,5.6 18.3,5.7 18.1,6 L16.4,8.9 C15.1,8.3 13.6,8 12,8 C10.4,8 8.9,8.3 7.6,8.9 L5.9,6 C5.7,5.7 5.3,5.6 5,5.8 C4.7,6 4.6,6.4 4.8,6.7 L6.4,9.5 C4.3,11.1 3,13.6 3,16.5 L21,16.5 C21,13.6 19.7,11.1 17.6,9.5 Z M8.5,13.5 A1,1 0 1,1 8.5,11.5 A1,1 0 1,1 8.5,13.5 Z M15.5,13.5 A1,1 0 1,1 15.5,11.5 A1,1 0 1,1 15.5,13.5 Z M5,18 L5,21 C5,21.6 5.4,22 6,22 C6.6,22 7,21.6 7,21 L7,18 L17,18 L17,21 C17,21.6 17.4,22 18,22 C18.6,22 19,21.6 19,21 L19,18 L21,18 L21,23 L3,23 L3,18 Z";
    public const string IosPath = MacPath;
    public const string UnknownPath =
        "M12,2 A10,10 0 1,0 12,22 A10,10 0 1,0 12,2 Z M11,7 L13,7 L13,13 L11,13 Z M11,15 L13,15 L13,17 L11,17 Z";

    public static string PlatformPath(string? platform) => CanonicalPlatformKey(platform) switch
    {
        "windows" => WindowsPath,
        "linux" => LinuxPath,
        "pc" or "windows/linux" => WindowsPath, // dual packs: show Windows mark (label spells both)
        "mac" or "macos" or "osx" or "pc/mac" or "windows/mac" => MacPath,
        "ios" or "iphone" or "ipad" => IosPath,
        "android" => AndroidPath,
        _ => UnknownPath
    };

    public static string PlatformLabel(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return "Unknown";
        return CanonicalPlatformKey(platform) switch
        {
            "windows" => "Windows",
            "linux" => "Linux",
            "pc" => "PC",
            "windows/linux" => "Windows/Linux",
            "mac" or "macos" or "osx" => "macOS",
            "pc/mac" => "PC/Mac",
            "windows/mac" => "Windows/Mac",
            "ios" or "iphone" or "ipad" => "iOS",
            "android" => "Android",
            _ => platform.Trim()
        };
    }

    public static string? FaviconUrl(string host)
    {
        var domain = host.Trim().ToLowerInvariant() switch
        {
            "gofile" => "gofile.io",
            "mega" => "mega.nz",
            "pixeldrain" => "pixeldrain.com",
            "datanodes" => "datanodes.to",
            "mediafire" => "mediafire.com",
            "workupload" => "workupload.com",
            "gdrive" or "drive" => "drive.google.com",
            "dropbox" => "dropbox.com",
            "catbox" => "catbox.moe",
            "bunkr" => "bunkr.si",
            "buzzheavier" => "buzzheavier.com",
            "mixdrop" => "mixdrop.ag",
            "uploadhaven" => "uploadhaven.com",
            "http" => null,
            _ => host.Contains('.') ? host : null
        };
        return domain is null
            ? null
            : $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=64";
    }

    public static IBrush PlatformBrush(string? platform) => CanonicalPlatformKey(platform) switch
    {
        "windows" => new SolidColorBrush(Color.Parse("#0078D4")),
        "linux" => new SolidColorBrush(Color.Parse("#E3A018")),
        "pc" or "windows/linux" => new SolidColorBrush(Color.Parse("#5B8DEF")),
        "mac" or "macos" or "osx" or "ios" or "iphone" or "ipad" or "pc/mac" or "windows/mac"
            => new SolidColorBrush(Color.Parse("#A0A8B8")),
        "android" => new SolidColorBrush(Color.Parse("#3DDC84")),
        _ => new SolidColorBrush(Color.Parse("#6B7C90"))
    };

    private static string CanonicalPlatformKey(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant().Replace(' ', '/');
        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);
        return p switch
        {
            "win" => "windows",
            "macos" or "osx" or "mac os" or "mac os x" => "mac",
            "win/linux" or "linux/windows" or "windows/linux" => "windows/linux",
            _ => p
        };
    }
}
