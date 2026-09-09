using AiProxy.Application.Interfaces;

namespace AiProxy.Services;

public sealed class ExecutablePathResolver : IExecutablePathResolver
{
    public string Resolve(string command)
    {
        if (File.Exists(command))
            return command;

        var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var pathFolders = new List<string>();
        pathFolders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries));
        pathFolders.AddRange(EnumerateKnownExecutableFolders());

        foreach (var folder in pathFolders)
        {
            foreach (var extension in pathExtensions)
            {
                var candidate = Path.Combine(folder, command + extension);
                if (File.Exists(candidate))
                    return candidate;

                if (!command.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidate = Path.Combine(folder, command);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    private static IEnumerable<string> EnumerateKnownExecutableFolders()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            yield break;

        yield return Path.Combine(userProfile, ".local", "bin");
        yield return Path.Combine(userProfile, ".grok", "bin");
        yield return Path.Combine(userProfile, "AppData", "Local", "agy", "bin");
        yield return Path.Combine(userProfile, "AppData", "Roaming", "npm");
        yield return Path.Combine(userProfile, "AppData", "Local", "Microsoft", "WindowsApps");
    }
}
