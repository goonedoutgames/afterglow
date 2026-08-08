using System.Diagnostics;
using Afterglow.Core;

namespace Afterglow.HubSidecar;

/// <summary>
/// Locates or builds avn-hub.exe for Local mode:
/// sidecar folder → AFTERGLOW_AVN_HUB_PATH → PATH → sibling repo binaries → cargo build.
/// </summary>
public static class SidecarBootstrap
{
    public static string SidecarExePath =>
        Path.Combine(AppPaths.SidecarDir, "avn-hub.exe");

    public static string? FindExistingExecutable()
    {
        if (File.Exists(SidecarExePath))
            return SidecarExePath;

        var env = Environment.GetEnvironmentVariable("AFTERGLOW_AVN_HUB_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var name in ExeNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var candidate in CandidateDevBinaries())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Ensures sidecar/avn-hub.exe exists, copying or building from the local avn-hub repo when possible.</summary>
    /// <param name="forceRebuild">When true and a sibling avn-hub repo is found, run cargo release build and reinstall even if an exe already exists.</param>
    public static async Task<string> EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRebuild = false)
    {
        Directory.CreateDirectory(AppPaths.SidecarDir);

        if (!forceRebuild && SidecarNeedsRebuild())
        {
            progress?.Report("Local hub source is newer than sidecar — rebuilding…");
            forceRebuild = true;
        }

        if (!forceRebuild)
        {
            var existing = FindExistingExecutable();
            if (existing is not null)
            {
                if (!PathsEqual(existing, SidecarExePath))
                {
                    progress?.Report($"Copying {existing} → sidecar…");
                    File.Copy(existing, SidecarExePath, overwrite: true);
                }
                progress?.Report($"Using {SidecarExePath}");
                return SidecarExePath;
            }
        }

        var repo = FindAvnHubRepoRoot();
        if (repo is null)
        {
            if (forceRebuild)
            {
                var existing = FindExistingExecutable();
                if (existing is not null)
                {
                    progress?.Report($"No avn-hub repo found to rebuild; keeping {existing}");
                    if (!PathsEqual(existing, SidecarExePath))
                        File.Copy(existing, SidecarExePath, overwrite: true);
                    return SidecarExePath;
                }
            }

            throw new FileNotFoundException(
                "Could not find avn-hub.exe. Place it in the sidecar folder, set AFTERGLOW_AVN_HUB_PATH, " +
                "or keep the avn-hub repo next to avn-hub-desktop so Afterglow can build it.");
        }

        progress?.Report($"Building avn-hub from {repo} (release)…");
        await CargoBuildAsync(repo, progress, cancellationToken);

        var built = CandidateDevBinaries(repo).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("cargo build finished but avn-hub.exe was not found under target/.");

        progress?.Report($"Installing {built} into sidecar…");
        File.Copy(built, SidecarExePath, overwrite: true);
        progress?.Report("Sidecar ready.");
        return SidecarExePath;
    }

    public static string? FindAvnHubRepoRoot()
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))
                 })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                // …/GitHub/avn-hub-desktop → sibling …/GitHub/avn-hub
                var sibling = Path.Combine(dir.FullName, "avn-hub");
                if (IsAvnHubRepo(sibling)) return sibling;

                var parentSibling = dir.Parent is null ? null : Path.Combine(dir.Parent.FullName, "avn-hub");
                if (parentSibling is not null && IsAvnHubRepo(parentSibling)) return parentSibling;

                if (IsAvnHubRepo(dir.FullName)) return dir.FullName;
            }
        }

        return null;
    }

    private static bool IsAvnHubRepo(string path) =>
        File.Exists(Path.Combine(path, "Cargo.toml"))
        && Directory.Exists(Path.Combine(path, "crates", "server"));

    /// <summary>True when sibling avn-hub crate sources are newer than the installed sidecar binary.</summary>
    public static bool SidecarNeedsRebuild()
    {
        var repo = FindAvnHubRepoRoot();
        if (repo is null) return false;

        var exe = FindExistingExecutable();
        var exeTime = exe is not null && File.Exists(exe)
            ? File.GetLastWriteTimeUtc(exe)
            : DateTime.MinValue;

        try
        {
            var crates = Path.Combine(repo, "crates");
            if (!Directory.Exists(crates)) return exe is null;
            var newestSource = Directory
                .EnumerateFiles(crates, "*.rs", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return newestSource > exeTime.AddSeconds(2);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidateDevBinaries(string? repoRoot = null)
    {
        var roots = new List<string>();
        if (repoRoot is not null) roots.Add(repoRoot);
        var found = FindAvnHubRepoRoot();
        if (found is not null && !roots.Contains(found)) roots.Add(found);

        foreach (var root in roots)
        {
            foreach (var config in new[] { "release", "debug" })
            foreach (var name in ExeNames)
                yield return Path.Combine(root, "target", config, name);
        }
    }

    private static readonly string[] ExeNames = ["avn-hub.exe", "avn-hub"];

    private static async Task CargoBuildAsync(string repoRoot, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var cargo = FindCargo() ?? throw new FileNotFoundException(
            "cargo was not found on PATH. Install Rust (rustup) or place a prebuilt avn-hub.exe in the sidecar folder.");

        var psi = new ProcessStartInfo(cargo)
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "build", "--release", "-p", "avn-hub-server", "--bin", "avn-hub" }
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start cargo.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var err = await stderr;
        var outText = await stdout;
        if (process.ExitCode != 0)
        {
            progress?.Report(err.Length > 0 ? err : outText);
            throw new InvalidOperationException($"cargo build failed (exit {process.ExitCode}). See status for details.");
        }
    }

    private static string? FindCargo()
    {
        foreach (var name in new[] { "cargo.exe", "cargo" })
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rustupCargo = Path.Combine(home, ".cargo", "bin", "cargo.exe");
        return File.Exists(rustupCargo) ? rustupCargo : null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
