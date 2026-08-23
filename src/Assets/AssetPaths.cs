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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find the 'assets' directory above the executable.");
    }
}
