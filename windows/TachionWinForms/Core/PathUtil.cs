using System.IO;
namespace Tachion.Core;

public static class PathUtil
{
    public static string RelativeUnixPath(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static string SafeFullPath(string root, string rel)
    {
        rel = rel.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root);
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes sync folder: " + rel);
        return full;
    }
}
