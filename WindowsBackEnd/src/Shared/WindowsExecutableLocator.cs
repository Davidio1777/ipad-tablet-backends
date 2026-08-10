using System.IO;

namespace IPadTablet.Shared;

public enum WindowsTool
{
    Ffmpeg,
    OpenTabletDriverConsole,
    Iproxy
}

public static class WindowsExecutableLocator
{
    public static string? Find(string? configured, WindowsTool tool, string? baseDirectory = null)
    {
        var names = CandidateNames(configured, tool).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var candidate in DirectCandidates(configured, names, baseDirectory ?? AppContext.BaseDirectory))
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        foreach (var root in SearchRoots(tool))
        {
            var found = SearchTree(root.Path, names, root.Depth);
            if (found is not null) return found;
        }
        return null;
    }

    private static IEnumerable<string> CandidateNames(string? configured, WindowsTool tool)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            yield return Path.GetFileName(configured.Trim());
        switch (tool)
        {
            case WindowsTool.Ffmpeg:
                yield return "ffmpeg.exe";
                break;
            case WindowsTool.OpenTabletDriverConsole:
                yield return "OpenTabletDriver.Console.exe";
                yield return "otd.exe";
                break;
            case WindowsTool.Iproxy:
                yield return "iproxy.exe";
                break;
        }
    }

    private static IEnumerable<string> DirectCandidates(
        string? configured, IReadOnlyCollection<string> names, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim();
            yield return Path.Combine(baseDirectory, configured.Trim());
        }

        var packageRoot = Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory;
        foreach (var name in names)
        {
            yield return Path.Combine(baseDirectory, name);
            yield return Path.Combine(packageRoot, name);
            yield return Path.Combine(packageRoot, "tools", name);
            yield return Path.Combine(packageRoot, "tools", "ffmpeg", name);
            yield return Path.Combine(packageRoot, "tools", "ffmpeg", "bin", name);
            yield return Path.Combine(packageRoot, "ffmpeg", "bin", name);
        }

        foreach (var pathDirectory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
                yield return Path.Combine(pathDirectory.Trim('"'), name);
    }

    private static IEnumerable<(string Path, int Depth)> SearchRoots(WindowsTool tool)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (tool == WindowsTool.Ffmpeg)
        {
            yield return (Path.Combine(user, "scoop", "apps", "ffmpeg"), 4);
            yield return (Path.Combine(local, "Microsoft", "WinGet", "Packages"), 5);
            yield return (Path.Combine(user, "Downloads"), 4);
        }
        if (tool == WindowsTool.OpenTabletDriverConsole)
        {
            // The official Windows instructions use a versioned directory
            // directly below the user's profile.
            yield return (user, 3);
            yield return (Path.Combine(user, "Downloads"), 4);
            yield return (Path.Combine(local, "OpenTabletDriver"), 3);
        }
        yield return (programs, 4);
        yield return (programsX86, 4);
    }

    private static string? SearchTree(string root, IReadOnlyCollection<string> names, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 5_000)
        {
            var (directory, depth) = pending.Dequeue();
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var childName = Path.GetFileName(child);
                    if (childName is "node_modules" or ".git" or "$Recycle.Bin"
                        || depth == 0 && childName.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
