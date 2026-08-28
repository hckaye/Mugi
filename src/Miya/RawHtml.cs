namespace Miya;

/// <summary>
/// An HTML fragment written without escaping. Create instances with <see cref="From"/>;
/// there is no implicit conversion from <see cref="string"/>.
/// </summary>
public readonly struct RawHtml
{
    private readonly string _html;

    private RawHtml(string html)
    {
        _html = html;
    }

    /// <summary>
    /// Marks <paramref name="html"/> as already-safe markup that should be written verbatim.
    /// </summary>
    public static RawHtml From(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new RawHtml(html);
    }

    internal string Value => _html;
}
