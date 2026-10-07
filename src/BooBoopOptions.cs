using System;
using System.IO;

namespace BooBoopBridge;

/// <summary>Installation paths. Empty values use the current machine/user's system folders.
/// Resolution validates paths without opening files, launching processes or connecting devices.</summary>
public sealed class BooBoopOptions
{
    public string HostExecutablePath { get; init; } = "";
    public string ProductCachePath { get; init; } = "";

    public string GetHostExecutablePath() => Resolve(HostExecutablePath,
        Environment.SpecialFolder.ProgramFiles, "Boo Boop", "Boo Boop.exe", executable: true);

    public string GetProductCachePath() => Resolve(ProductCachePath,
        Environment.SpecialFolder.ApplicationData, "BooBoop", "product_list.json", executable: false);

    public string GetHostWorkingDirectory() => Path.GetDirectoryName(GetHostExecutablePath())!;

    private static string Resolve(string configured, Environment.SpecialFolder folder,
        string subdirectory, string filename, bool executable)
    {
        string path = configured?.Trim() ?? "";
        if (path.Length == 0)
        {
            string baseDirectory = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new InvalidOperationException("System installation folder is unavailable; configure an absolute path.");
            path = Path.Combine(baseDirectory, subdirectory, filename);
        }
        path = Environment.ExpandEnvironmentVariables(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Configure a fully qualified file path; relative paths are not supported.");
        path = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(path)))
            throw new ArgumentException("Configure a file path, not a directory.");
        if (executable && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("HostExecutablePath must refer to the vendor .exe file.");
        return path;
    }
}
