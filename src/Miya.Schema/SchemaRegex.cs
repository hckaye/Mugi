using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Miya.Schema;

/// <summary>Creates and evaluates regular expressions used by generated schema binders.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SchemaRegex
{
    /// <summary>Creates a culture-invariant regular expression with bounded matching behavior.</summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <returns>A regular expression instance.</returns>
    public static Regex Create(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            return new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Tests an input and reports a timeout as a failed match.</summary>
    /// <param name="regex">The regular expression to evaluate.</param>
    /// <param name="input">The input to test.</param>
    /// <returns><see langword="true"/> when the input matches; otherwise, <see langword="false"/>.</returns>
    public static bool IsMatch(Regex regex, string input)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
