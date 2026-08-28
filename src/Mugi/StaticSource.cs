using System.Reflection;

namespace Mugi;

/// <summary>Describes an embedded resource source for a static file route.</summary>
public abstract class StaticSource
{
    /// <summary>Creates a source that serves resources from an assembly.</summary>
    /// <param name="assembly">The assembly containing the resources.</param>
    /// <param name="prefix">The resource name prefix to remove before mapping URLs.</param>
    /// <returns>An embedded resource source.</returns>
    /// <remarks>
    /// Resource names containing '/' are mapped verbatim after the prefix and '/' are removed.
    /// Default MSBuild dotted resource names use dots in directory names and keep the final dot as
    /// the filename extension separator. MSBuild can replace '-' with '_', so use an explicit
    /// LogicalName when a URL must preserve that character.
    /// </remarks>
    public static StaticSource Embedded(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(prefix);
        return new EmbeddedStaticSource(assembly, NormalizePrefix(prefix));
    }

    private static string NormalizePrefix(string prefix)
    {
        var start = 0;
        while (start < prefix.Length && prefix[start] == '/')
        {
            start++;
        }

        var end = prefix.Length;
        while (end > start && prefix[end - 1] == '/')
        {
            end--;
        }

        return start == 0 && end == prefix.Length
            ? prefix
            : prefix[start..end];
    }
}

internal sealed class EmbeddedStaticSource : StaticSource
{
    internal EmbeddedStaticSource(Assembly assembly, string prefix)
    {
        Assembly = assembly;
        Prefix = prefix;
    }

    internal Assembly Assembly { get; }

    internal string Prefix { get; }

    internal EmbeddedStaticSnapshot CreateSnapshot()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in Assembly.GetManifestResourceNames())
        {
            if (!TryMapResource(resourceName, out var path))
            {
                continue;
            }

            resources.TryAdd(path, resourceName);
        }

        return new EmbeddedStaticSnapshot(
            Assembly,
            Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            resources);
    }

    private bool TryMapResource(string resourceName, out string path)
    {
        if (resourceName.IndexOf('/') >= 0)
        {
            return TryMapLogicalResource(resourceName, out path);
        }

        if (!TryStripDottedPrefix(resourceName, Prefix, out var remainder))
        {
            path = string.Empty;
            return false;
        }

        path = ConvertDottedResourcePath(remainder);
        return path.Length > 0;
    }

    private bool TryMapLogicalResource(string resourceName, out string path)
    {
        if (Prefix.Length == 0)
        {
            path = resourceName;
            return path.Length > 0;
        }

        var marker = string.Concat(Prefix, "/");
        if (!resourceName.StartsWith(marker, StringComparison.Ordinal))
        {
            path = string.Empty;
            return false;
        }

        path = resourceName[marker.Length..];
        return path.Length > 0;
    }

    private static bool TryStripDottedPrefix(
        string resourceName,
        string prefix,
        out string remainder)
    {
        if (prefix.Length == 0)
        {
            remainder = resourceName;
            return remainder.Length > 0;
        }

        var marker = string.Concat(prefix, ".");
        if (resourceName.StartsWith(marker, StringComparison.Ordinal))
        {
            remainder = resourceName[marker.Length..];
            return remainder.Length > 0;
        }

        var searchStart = 0;
        while (searchStart < resourceName.Length)
        {
            var index = resourceName.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var end = index + prefix.Length;
            var hasSegmentStart = index == 0 || resourceName[index - 1] == '.';
            var hasSegmentEnd = end < resourceName.Length && resourceName[end] == '.';
            if (hasSegmentStart && hasSegmentEnd)
            {
                remainder = resourceName[(end + 1)..];
                return remainder.Length > 0;
            }

            searchStart = index + 1;
        }

        remainder = string.Empty;
        return false;
    }

    private static string ConvertDottedResourcePath(string resourceName)
    {
        var extensionSeparator = resourceName.LastIndexOf('.');
        if (extensionSeparator <= 0 || extensionSeparator == resourceName.Length - 1)
        {
            return resourceName;
        }

        var characters = resourceName.ToCharArray();
        for (var index = 0; index < extensionSeparator; index++)
        {
            if (characters[index] == '.')
            {
                characters[index] = '/';
            }
        }

        return new string(characters);
    }
}

internal sealed class EmbeddedStaticSnapshot
{
    internal EmbeddedStaticSnapshot(
        Assembly assembly,
        string moduleVersionId,
        Dictionary<string, string> resources)
    {
        Assembly = assembly;
        ModuleVersionId = moduleVersionId;
        Resources = resources;
    }

    internal Assembly Assembly { get; }

    internal string ModuleVersionId { get; }

    internal Dictionary<string, string> Resources { get; }
}
