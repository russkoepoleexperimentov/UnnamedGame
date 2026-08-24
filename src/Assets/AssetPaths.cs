namespace UnnamedGame.Assets;

/// <summary>Finds the assets folder by walking up from the executable, so the 90 MB of
/// models and textures never has to be copied into the build output.</summary>
public static class AssetPaths
{
    public static string Root { get; } = Locate();

    public static string Get(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Config lives next to the assets folder, so it survives rebuilds of bin/.</summary>
    public static string ConfigFile { get; } = Path.Combine(Directory.GetParent(Root)!.FullName, "config.cfg");

    private static string Locate()
    {
        var candidates = new List<string>();

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets");
            if (Directory.Exists(candidate)) candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            throw new DirectoryNotFoundException("Could not find the 'assets' directory above the executable.");

        // In a source tree a copy of the assets can end up inside bin\, and taking the nearest
        // one would silently shadow the real folder. The one sitting next to the project files
        // is the authoritative copy; a deployed build has only one candidate anyway.
        foreach (var candidate in candidates)
        {
            var parent = Directory.GetParent(candidate);
            if (parent is null) continue;
            if (parent.EnumerateFiles("*.sln").Any() || parent.EnumerateFiles("*.csproj").Any())
                return candidate;
        }

        return candidates[0];
    }
}
