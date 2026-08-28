using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Security;
using Microsoft.AspNetCore.Http;

namespace Mugi;

internal sealed class StaticHandler<TContext>
    where TContext : Context, new()
{
    private readonly App<TContext> _app;
    private readonly StaticRegistration _registration;

    internal StaticHandler(App<TContext> app, StaticRegistration registration)
    {
        _app = app;
        _registration = registration;
    }

    internal ValueTask InvokeRoot(TContext context) =>
        _registration.Serve(context, _app, string.Empty);

    internal ValueTask InvokePath(TContext context) =>
        _registration.Serve(context, _app, context.Param("path"));
}

internal sealed class StaticRegistration
{
    private const int StaticBufferSize = 64 * 1024;

    private readonly string? _root;
    private readonly string? _rootPrefix;
    private readonly string? _resolvedRootPrefix;
    private readonly EmbeddedStaticSnapshot? _embedded;
    private readonly string _index;
    private readonly string? _cacheControl;
    private readonly bool _precompressed;

    private StaticRegistration(
        string root,
        string index,
        string? cacheControl,
        bool precompressed)
    {
        _root = root;
        _rootPrefix = AddDirectorySeparator(root);
        _resolvedRootPrefix = TryResolveLinks(root, out var resolvedRoot)
            ? AddDirectorySeparator(resolvedRoot)
            : null;
        _index = index;
        _cacheControl = cacheControl;
        _precompressed = precompressed;
    }

    private StaticRegistration(
        EmbeddedStaticSnapshot embedded,
        string index,
        string? cacheControl)
    {
        _embedded = embedded;
        _index = index;
        _cacheControl = cacheControl;
        _precompressed = false;
    }

    internal static StaticRegistration Create(StaticOptions options)
    {
        var root = options.Root;
        var source = options.Source;
        if ((root is null) == (source is null))
        {
            throw new ArgumentException(
                "StaticOptions must specify exactly one of Root or Source.",
                nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Index);
        ValidateCacheControl(options.CacheControl);
        if (!IsSafeRelativePath(options.Index, allowEmpty: true))
        {
            throw new ArgumentException(
                "The static index path must be a relative path without traversal segments.",
                nameof(options));
        }

        if (root is not null)
        {
            if (root.Length == 0 || root.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("The static root must be a valid filesystem path.", nameof(options));
            }

            var fullRoot = Path.GetFullPath(root);
            return new StaticRegistration(
                fullRoot,
                options.Index,
                options.CacheControl,
                options.Precompressed);
        }

        if (source is not EmbeddedStaticSource embeddedSource)
        {
            throw new ArgumentException("The static source is not supported.", nameof(options));
        }

        return new StaticRegistration(
            embeddedSource.CreateSnapshot(),
            options.Index,
            options.CacheControl);
    }

    internal ValueTask Serve<TContext>(
        TContext context,
        App<TContext> app,
        string requestPath)
        where TContext : Context, new()
    {
        if (_embedded is not null)
        {
            return ServeEmbedded(context, app, requestPath);
        }

        return ServeFileSystem(context, app, requestPath);
    }

    private ValueTask ServeFileSystem<TContext>(
        TContext context,
        App<TContext> app,
        string requestPath)
        where TContext : Context, new()
    {
        if (!TryResolveFilePath(requestPath, out var fullPath, out var relativePath))
        {
            return app.InvokeStaticNotFound(context);
        }

        if ((requestPath.Length == 0 || requestPath[^1] != '/')
            && Directory.Exists(fullPath))
        {
            return app.InvokeStaticNotFound(context);
        }

        var preferences = _precompressed
            ? EncodingPreferences.Parse(context.Req.Header("Accept-Encoding"))
            : default;
        if (!TryOpenFile(fullPath, preferences, out var opened, out var contentEncoding))
        {
            return app.InvokeStaticNotFound(context);
        }

        var contentType = ContentTypes.Get(relativePath);
        return ServeFile(context, opened, contentType, contentEncoding);
    }

    private ValueTask ServeEmbedded<TContext>(
        TContext context,
        App<TContext> app,
        string requestPath)
        where TContext : Context, new()
    {
        if (!TryResolveResourcePath(requestPath, out var resourcePath)
            || !_embedded!.Resources.TryGetValue(resourcePath, out var resourceName))
        {
            return app.InvokeStaticNotFound(context);
        }

        Stream? stream;
        try
        {
            stream = _embedded.Assembly.GetManifestResourceStream(resourceName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return app.InvokeStaticNotFound(context);
        }

        if (stream is null)
        {
            return app.InvokeStaticNotFound(context);
        }

        long length;
        try
        {
            length = stream.Length;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            stream.Dispose();
            return app.InvokeStaticNotFound(context);
        }

        var etag = CreateResourceEtag(_embedded.ModuleVersionId, resourceName);
        if (ConditionalRequests.IsNotModified(context, etag, lastModified: null))
        {
            stream.Dispose();
            return SendNotModified(context, etag, lastModified: null, supportsRanges: false, contentEncoding: null);
        }

        SetValidatorHeaders(context, etag, lastModified: null, supportsRanges: false, contentEncoding: null);
        context.Status(StatusCodes.Status200OK);
        return StreamBody(context, stream, offset: 0, length, ContentTypes.Get(resourcePath));
    }

    private ValueTask ServeFile(
        Context context,
        OpenedStaticFile opened,
        string contentType,
        string? contentEncoding)
    {
        var etag = opened.ETag;
        var lastModified = opened.LastModified;
        if (ConditionalRequests.IsNotModified(context, etag, lastModified))
        {
            opened.Dispose();
            return SendNotModified(context, etag, lastModified, supportsRanges: true, contentEncoding);
        }

        var range = RangeRequest.Parse(context.Req.Header("Range"), opened.Length);
        if (range.Result != RangeResult.Full
            && !ConditionalRequests.IsRangeAllowed(context, etag, lastModified))
        {
            range = RangeRequest.Full;
        }

        SetValidatorHeaders(context, etag, lastModified, supportsRanges: true, contentEncoding);
        if (range.Result == RangeResult.Unsatisfiable)
        {
            opened.Dispose();
            context.Header("Content-Range", string.Concat("bytes */", opened.Length.ToString(CultureInfo.InvariantCulture)));
            context.Status(StatusCodes.Status416RangeNotSatisfiable);
            context.SetEmptyBody();
            return ValueTask.CompletedTask;
        }

        if (range.Result == RangeResult.Partial)
        {
            context.Status(StatusCodes.Status206PartialContent);
            var end = checked(range.Start + range.Length - 1);
            context.Header(
                "Content-Range",
                string.Concat(
                    "bytes ",
                    range.Start.ToString(CultureInfo.InvariantCulture),
                    "-",
                    end.ToString(CultureInfo.InvariantCulture),
                    "/",
                    opened.Length.ToString(CultureInfo.InvariantCulture)));
            return StreamBody(context, opened.Stream, range.Start, range.Length, contentType);
        }

        context.Status(StatusCodes.Status200OK);
        return StreamBody(context, opened.Stream, offset: 0, opened.Length, contentType);
    }

    private ValueTask StreamBody(
        Context context,
        Stream stream,
        long offset,
        long length,
        string contentType)
    {
        var body = new StaticStreamBody(stream, offset, length);
        return StreamBodyAsync(context, body, contentType);
    }

    private static async ValueTask StreamBodyAsync(
        Context context,
        StaticStreamBody body,
        string contentType)
    {
        try
        {
            await context.Stream(contentType, body.Length, body.WriteAsync).ConfigureAwait(false);
        }
        finally
        {
            body.Dispose();
        }
    }

    private bool TryResolveFilePath(
        string requestPath,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        if (!TryResolveRelativePath(requestPath, out relativePath))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_root!, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return fullPath.StartsWith(_rootPrefix!, StringComparison.Ordinal)
            && TryResolveContainedPath(fullPath, out fullPath);
    }

    private bool TryResolveResourcePath(string requestPath, out string resourcePath) =>
        TryResolveRelativePath(requestPath, out resourcePath);

    private bool TryResolveRelativePath(string requestPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (!IsSafeRelativePath(requestPath, allowEmpty: true))
        {
            return false;
        }

        if (requestPath.Length == 0 || requestPath[^1] == '/')
        {
            if (_index.Length == 0)
            {
                return false;
            }

            relativePath = string.Concat(requestPath, _index);
        }
        else
        {
            relativePath = requestPath;
        }

        return IsSafeRelativePath(relativePath, allowEmpty: false);
    }

    private bool TryOpenFile(
        string fullPath,
        EncodingPreferences preferences,
        out OpenedStaticFile opened,
        out string? contentEncoding)
    {
        opened = default;
        contentEncoding = null;
        if (_precompressed && preferences.GzipQuality > preferences.BrQuality)
        {
            if (preferences.GzipAccepted
                && TryOpenFile(string.Concat(fullPath, ".gz"), out opened))
            {
                contentEncoding = "gzip";
                return true;
            }

            if (preferences.BrAccepted
                && TryOpenFile(string.Concat(fullPath, ".br"), out opened))
            {
                contentEncoding = "br";
                return true;
            }
        }
        else if (_precompressed)
        {
            if (preferences.BrAccepted
                && TryOpenFile(string.Concat(fullPath, ".br"), out opened))
            {
                contentEncoding = "br";
                return true;
            }

            if (preferences.GzipAccepted
                && TryOpenFile(string.Concat(fullPath, ".gz"), out opened))
            {
                contentEncoding = "gzip";
                return true;
            }
        }

        return TryOpenFile(fullPath, out opened);
    }

    private bool TryOpenFile(string path, out OpenedStaticFile opened)
    {
        opened = default;
        if (!TryResolveContainedPath(path, out path))
        {
            return false;
        }

        DateTime lastWriteTimeUtc;
        try
        {
            lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            return false;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StaticBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            opened = new OpenedStaticFile(
                stream,
                length,
                TruncateToHttpSecond(new DateTimeOffset(lastWriteTimeUtc)),
                lastWriteTimeUtc.Ticks);
            stream = null;
            return true;
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void SetValidatorHeaders(
        Context context,
        string etag,
        DateTimeOffset? lastModified,
        bool supportsRanges,
        string? contentEncoding)
    {
        context.Header("ETag", etag);
        if (lastModified.HasValue)
        {
            context.Header("Last-Modified", FormatHttpDate(lastModified.Value));
        }

        if (_cacheControl is not null)
        {
            context.Header("Cache-Control", _cacheControl);
        }

        if (supportsRanges)
        {
            context.Header("Accept-Ranges", "bytes");
        }

        if (contentEncoding is not null)
        {
            context.Header("Content-Encoding", contentEncoding);
            context.Header("Vary", "Accept-Encoding");
        }
    }

    private ValueTask SendNotModified(
        Context context,
        string etag,
        DateTimeOffset? lastModified,
        bool supportsRanges,
        string? contentEncoding)
    {
        SetValidatorHeaders(context, etag, lastModified, supportsRanges, contentEncoding);
        context.Status(StatusCodes.Status304NotModified);
        context.SetEmptyBody();
        return ValueTask.CompletedTask;
    }

    private static bool IsSafeRelativePath(string value, bool allowEmpty)
    {
        if (!allowEmpty && value.Length == 0)
        {
            return false;
        }

        if (value.IndexOf('\0') >= 0
            || value.IndexOf('\\') >= 0
            || (value.Length > 0 && value[0] == '/')
            || Path.IsPathRooted(value)
            || IsDrivePath(value))
        {
            return false;
        }

        var segmentStart = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] != '/')
            {
                continue;
            }

            var segment = value.AsSpan(segmentStart, index - segmentStart);
            if (segment.SequenceEqual(".".AsSpan())
                || segment.SequenceEqual("..".AsSpan())
                || IsDotOnlySegmentLongerThanTwo(segment))
            {
                return false;
            }

            segmentStart = index + 1;
        }

        return true;
    }

    private static bool IsDrivePath(string value) =>
        value.Length >= 2
        && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z'))
        && value[1] == ':';

    private static bool IsDotOnlySegmentLongerThanTwo(ReadOnlySpan<char> segment)
    {
        if (segment.Length <= 2)
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateCacheControl(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\0' or '\u007f'
                || (character < ' ' && character != '\t'))
            {
                throw new ArgumentException("The cache-control value contains an invalid character.", nameof(value));
            }
        }
    }

    private static string AddDirectorySeparator(string path)
    {
        if (path.Length > 0
            && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return string.Concat(path, Path.DirectorySeparatorChar);
    }

    private bool TryResolveContainedPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        return _resolvedRootPrefix is not null
            && TryResolveLinks(path, out resolvedPath)
            && resolvedPath.StartsWith(_resolvedRootPrefix, StringComparison.Ordinal);
    }

    private static bool TryResolveLinks(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        string fullPath;
        string? pathRoot;
        try
        {
            fullPath = Path.GetFullPath(path);
            pathRoot = Path.GetPathRoot(fullPath);
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            return false;
        }

        if (string.IsNullOrEmpty(pathRoot))
        {
            return false;
        }

        var current = pathRoot;
        var position = pathRoot.Length;
        try
        {
            while (position < fullPath.Length)
            {
                while (position < fullPath.Length
                    && (fullPath[position] == Path.DirectorySeparatorChar
                        || fullPath[position] == Path.AltDirectorySeparatorChar))
                {
                    position++;
                }

                if (position == fullPath.Length)
                {
                    break;
                }

                var end = position;
                while (end < fullPath.Length
                    && fullPath[end] != Path.DirectorySeparatorChar
                    && fullPath[end] != Path.AltDirectorySeparatorChar)
                {
                    end++;
                }

                current = Path.Combine(current, fullPath[position..end]);
                FileSystemInfo? entry = null;
                if (Directory.Exists(current))
                {
                    entry = new DirectoryInfo(current);
                }
                else if (File.Exists(current))
                {
                    entry = new FileInfo(current);
                }

                var target = entry?.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    if (!TryResolveLinks(target.FullName, out current))
                    {
                        return false;
                    }
                }

                position = end + 1;
            }

            resolvedPath = Path.GetFullPath(current);
            return true;
        }
        catch (Exception exception) when (IsFileAccessFailure(exception))
        {
            return false;
        }
    }

    private static DateTimeOffset TruncateToHttpSecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static string FormatHttpDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

    private static string CreateResourceEtag(string moduleVersionId, string resourceName)
    {
        var hash = 2166136261u;
        foreach (var character in resourceName)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return string.Concat("\"", moduleVersionId, "-", hash.ToString("x8", CultureInfo.InvariantCulture), "\"");
    }

    private static bool IsFileAccessFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException;

    private readonly struct OpenedStaticFile : IDisposable
    {
        internal OpenedStaticFile(
            FileStream stream,
            long length,
            DateTimeOffset lastModified,
            long etagTicks)
        {
            Stream = stream;
            Length = length;
            LastModified = lastModified;
            ETag = string.Concat(
                "\"",
                etagTicks.ToString("x", CultureInfo.InvariantCulture),
                "-",
                length.ToString("x", CultureInfo.InvariantCulture),
                "\"");
        }

        internal FileStream Stream { get; }

        internal long Length { get; }

        internal DateTimeOffset LastModified { get; }

        internal string ETag { get; }

        public void Dispose() => Stream.Dispose();
    }
}

internal static class ContentTypes
{
    internal static string Get(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= lastSlash || lastDot == path.Length - 1)
        {
            return "application/octet-stream";
        }

        var extension = path.AsSpan(lastDot + 1);
        if (extension.Equals("html".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("htm".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "text/html; charset=utf-8";
        }

        if (extension.Equals("css".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "text/css; charset=utf-8";
        }

        if (extension.Equals("js".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("mjs".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("cjs".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "text/javascript; charset=utf-8";
        }

        if (extension.Equals("json".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "application/json; charset=utf-8";
        }

        if (extension.Equals("svg".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml; charset=utf-8";
        }

        if (extension.Equals("xml".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml; charset=utf-8";
        }

        if (extension.Equals("png".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (extension.Equals("jpg".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("jpeg".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (extension.Equals("gif".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/gif";
        if (extension.Equals("webp".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (extension.Equals("avif".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/avif";
        if (extension.Equals("ico".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/x-icon";
        if (extension.Equals("bmp".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        if (extension.Equals("tif".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("tiff".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/tiff";
        if (extension.Equals("apng".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "image/apng";
        if (extension.Equals("woff".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "font/woff";
        if (extension.Equals("woff2".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "font/woff2";
        if (extension.Equals("ttf".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "font/ttf";
        if (extension.Equals("otf".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "font/otf";
        if (extension.Equals("eot".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/vnd.ms-fontobject";

        if (extension.Equals("txt".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("text".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "text/plain; charset=utf-8";
        if (extension.Equals("md".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("markdown".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "text/markdown; charset=utf-8";
        if (extension.Equals("csv".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "text/csv; charset=utf-8";
        if (extension.Equals("yaml".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("yml".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/yaml; charset=utf-8";

        if (extension.Equals("pdf".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/pdf";
        if (extension.Equals("wasm".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/wasm";
        if (extension.Equals("map".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (extension.Equals("webmanifest".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/manifest+json";
        if (extension.Equals("manifest".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/manifest+json";
        if (extension.Equals("mp4".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("m4v".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (extension.Equals("webm".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (extension.Equals("mov".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (extension.Equals("mp3".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
        if (extension.Equals("wav".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "audio/wav";
        if (extension.Equals("flac".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "audio/flac";
        if (extension.Equals("ogg".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || extension.Equals("oga".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "audio/ogg";
        if (extension.Equals("opus".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "audio/opus";
        if (extension.Equals("ogv".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "video/ogg";
        if (extension.Equals("zip".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/zip";
        if (extension.Equals("tar".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/x-tar";
        if (extension.Equals("gz".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/gzip";
        if (extension.Equals("br".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
        if (extension.Equals("7z".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/x-7z-compressed";
        if (extension.Equals("rar".AsSpan(), StringComparison.OrdinalIgnoreCase)) return "application/vnd.rar";

        return "application/octet-stream";
    }
}

internal readonly struct EncodingPreferences
{
    private readonly int _brQuality;
    private readonly int _gzipQuality;
    private readonly int _wildcardQuality;
    private readonly bool _hasBr;
    private readonly bool _hasGzip;
    private readonly bool _hasWildcard;

    private EncodingPreferences(
        int brQuality,
        int gzipQuality,
        int wildcardQuality,
        bool hasBr,
        bool hasGzip,
        bool hasWildcard)
    {
        _brQuality = brQuality;
        _gzipQuality = gzipQuality;
        _wildcardQuality = wildcardQuality;
        _hasBr = hasBr;
        _hasGzip = hasGzip;
        _hasWildcard = hasWildcard;
    }

    internal int BrQuality => _hasBr ? _brQuality : _hasWildcard ? _wildcardQuality : 0;

    internal int GzipQuality => _hasGzip ? _gzipQuality : _hasWildcard ? _wildcardQuality : 0;

    internal bool BrAccepted => BrQuality > 0;

    internal bool GzipAccepted => GzipQuality > 0;

    internal static EncodingPreferences Parse(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return default;
        }

        var brQuality = 0;
        var gzipQuality = 0;
        var wildcardQuality = 0;
        var hasBr = false;
        var hasGzip = false;
        var hasWildcard = false;
        var start = 0;
        while (start <= header.Length)
        {
            var comma = header.IndexOf(',', start);
            var end = comma < 0 ? header.Length : comma;
            var item = header.AsSpan(start, end - start).Trim();
            if (item.Length > 0)
            {
                var semicolon = item.IndexOf(';');
                var coding = (semicolon < 0 ? item : item[..semicolon]).Trim();
                var quality = semicolon < 0 ? 1000 : ParseQuality(item[(semicolon + 1)..]);
                if (coding.Equals("br".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    hasBr = true;
                    brQuality = quality;
                }
                else if (coding.Equals("gzip".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    hasGzip = true;
                    gzipQuality = quality;
                }
                else if (coding.SequenceEqual("*".AsSpan()))
                {
                    hasWildcard = true;
                    wildcardQuality = quality;
                }
            }

            if (comma < 0)
            {
                break;
            }

            start = comma + 1;
        }

        return new EncodingPreferences(
            brQuality,
            gzipQuality,
            wildcardQuality,
            hasBr,
            hasGzip,
            hasWildcard);
    }

    private static int ParseQuality(ReadOnlySpan<char> parameters)
    {
        var start = 0;
        while (start <= parameters.Length)
        {
            var semicolon = parameters[start..].IndexOf(';');
            var end = semicolon < 0 ? parameters.Length : start + semicolon;
            var parameter = parameters[start..end].Trim();
            var equals = parameter.IndexOf('=');
            if (equals >= 0
                && parameter[..equals].Trim().Equals("q".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return ParseQualityValue(parameter[(equals + 1)..].Trim());
            }

            if (semicolon < 0)
            {
                break;
            }

            start = end + 1;
        }

        return 1000;
    }

    private static int ParseQualityValue(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        if (value[0] == '1')
        {
            if (value.Length == 1)
            {
                return 1000;
            }

            if (value[1] != '.' || value.Length > 5)
            {
                return 0;
            }

            for (var index = 2; index < value.Length; index++)
            {
                if (value[index] != '0')
                {
                    return 0;
                }
            }

            return 1000;
        }

        if (value[0] != '0')
        {
            return 0;
        }

        if (value.Length == 1)
        {
            return 0;
        }

        if (value[1] != '.' || value.Length > 5)
        {
            return 0;
        }

        var quality = 0;
        var multiplier = 100;
        for (var index = 2; index < value.Length; index++)
        {
            var digit = value[index] - '0';
            if (digit is < 0 or > 9)
            {
                return 0;
            }

            quality += digit * multiplier;
            multiplier /= 10;
        }

        return quality;
    }
}

internal enum RangeResult
{
    Full,
    Partial,
    Unsatisfiable,
}

internal readonly struct RangeRequest
{
    internal static readonly RangeRequest Full = new(RangeResult.Full, 0, 0);

    private RangeRequest(RangeResult result, long start, long length)
    {
        Result = result;
        Start = start;
        Length = length;
    }

    internal RangeResult Result { get; }

    internal long Start { get; }

    internal long Length { get; }

    internal static RangeRequest Parse(string? header, long totalLength)
    {
        if (string.IsNullOrEmpty(header))
        {
            return Full;
        }

        var value = header.AsSpan().Trim();
        if (!value.StartsWith("bytes=".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return Full;
        }

        var specification = value[6..].Trim();
        if (specification.Length == 0 || specification.IndexOf(',') >= 0)
        {
            return Full;
        }

        var dash = specification.IndexOf('-');
        if (dash < 0 || specification[(dash + 1)..].IndexOf('-') >= 0)
        {
            return Full;
        }

        var first = specification[..dash].Trim();
        var second = specification[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!TryParseNonNegative(second, out var suffixLength))
            {
                return Full;
            }

            if (suffixLength == 0 || totalLength == 0)
            {
                return new RangeRequest(RangeResult.Unsatisfiable, 0, 0);
            }

            var length = Math.Min(suffixLength, totalLength);
            return new RangeRequest(RangeResult.Partial, totalLength - length, length);
        }

        if (!TryParseNonNegative(first, out var start))
        {
            return Full;
        }

        if (start >= totalLength || totalLength == 0)
        {
            return new RangeRequest(RangeResult.Unsatisfiable, 0, 0);
        }

        if (second.Length == 0)
        {
            return new RangeRequest(RangeResult.Partial, start, totalLength - start);
        }

        if (!TryParseNonNegative(second, out var end))
        {
            return Full;
        }

        if (start > end)
        {
            return new RangeRequest(RangeResult.Unsatisfiable, 0, 0);
        }

        end = Math.Min(end, totalLength - 1);
        return new RangeRequest(RangeResult.Partial, start, end - start + 1);
    }

    private static bool TryParseNonNegative(ReadOnlySpan<char> value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= 0;
}

internal static class ConditionalRequests
{
    internal static bool IsNotModified(
        Context context,
        string etag,
        DateTimeOffset? lastModified)
    {
        var ifNoneMatch = context.Req.Header("If-None-Match");
        if (ifNoneMatch is not null)
        {
            return MatchesEntityTagList(ifNoneMatch, etag, allowWeak: true);
        }

        if (!lastModified.HasValue)
        {
            return false;
        }

        var ifModifiedSince = context.Req.Header("If-Modified-Since");
        return ifModifiedSince is not null
            && TryParseHttpDate(ifModifiedSince, out var date)
            && lastModified.Value <= date;
    }

    internal static bool IsRangeAllowed(
        Context context,
        string etag,
        DateTimeOffset lastModified)
    {
        var ifRange = context.Req.Header("If-Range");
        if (ifRange is null)
        {
            return true;
        }

        var value = ifRange.AsSpan().Trim();
        if (value.StartsWith("W/".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.SequenceEqual(etag.AsSpan()))
        {
            return true;
        }

        return TryParseHttpDate(ifRange, out var date) && lastModified <= date;
    }

    private static bool MatchesEntityTagList(string value, string etag, bool allowWeak)
    {
        var start = 0;
        while (start <= value.Length)
        {
            var comma = value.IndexOf(',', start);
            var end = comma < 0 ? value.Length : comma;
            var candidate = value.AsSpan(start, end - start).Trim();
            if (candidate.SequenceEqual("*".AsSpan()))
            {
                return true;
            }

            if (allowWeak && candidate.StartsWith("W/".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[2..].TrimStart();
            }

            if (candidate.SequenceEqual(etag.AsSpan()))
            {
                return true;
            }

            if (comma < 0)
            {
                break;
            }

            start = comma + 1;
        }

        return false;
    }

    private static bool TryParseHttpDate(string value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }
}

internal sealed class StaticStreamBody : IDisposable
{
    private readonly Stream _stream;
    private readonly long _offset;

    internal StaticStreamBody(Stream stream, long offset, long length)
    {
        _stream = stream;
        _offset = offset;
        Length = length;
    }

    internal long Length { get; }

    internal ValueTask WriteAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        WriteCoreAsync(writer, cancellationToken);

    public void Dispose() => _stream.Dispose();

    private async ValueTask WriteCoreAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        if (_offset != 0)
        {
            _stream.Seek(_offset, SeekOrigin.Begin);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var read = await _stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The static file changed while it was being served.");
                }

                writer.Write(buffer.AsSpan(0, read));
                var flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
