using System.Reflection;

namespace Afterglow.Core;

public static class AppVersionInfo
{
    /// <summary>Running app version from InformationalVersion / Assembly version (e.g. 0.1.14 or 0.0.0-dev).</summary>
    public static string Current { get; } = Resolve();

    public static bool IsReleaseBuild =>
        SemVer.TryParse(Current, out _, out var pre) && !pre;

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return SemVer.Normalize(info);

        var ver = asm.GetName().Version;
        if (ver is null) return "0.0.0-dev";
        return $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
    }
}
