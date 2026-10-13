namespace Jellyfin.Plugin.XRay.Analysis;

/// <summary>
/// Matches media paths against the folders analysis is limited to.
/// </summary>
public static class FolderFilter
{
    private static readonly StringComparison PathComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Splits the configured folder list (one path per line).
    /// </summary>
    /// <param name="folders">Configured value.</param>
    /// <returns>Non-empty folder paths.</returns>
    public static IReadOnlyList<string> Parse(string? folders)
        => (folders ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// Gets a value indicating whether a file lies in a folder (at any depth).
    /// </summary>
    /// <param name="folder">Folder path.</param>
    /// <param name="path">File path.</param>
    /// <returns>True when <paramref name="path"/> is inside <paramref name="folder"/>.</returns>
    public static bool Contains(string folder, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var root = Normalize(folder);
        var file = Normalize(path);
        // "/" normalizes to "" and covers every absolute path.
        return file.StartsWith(root, PathComparison)
            && (file.Length == root.Length || file[root.Length] == '/');
    }

    /// <summary>
    /// Gets a value indicating whether a file lies in any of the folders. An empty list matches everything.
    /// </summary>
    /// <param name="folders">Folder paths.</param>
    /// <param name="path">File path.</param>
    /// <returns>True when included.</returns>
    public static bool Includes(IReadOnlyList<string> folders, string? path)
        => folders.Count == 0 || folders.Any(f => Contains(f, path));

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
