using System.ComponentModel;
using System.Globalization;
using System.Numerics;

namespace Miya.Schema;

/// <summary>Parses culture-invariant text values used by generated schema binders.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SchemaText
{
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "O",
    ];

    private static readonly string[] DateTimeOffsetWithoutOffsetFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] DateTimeOffsetWithOffsetFormats =
    [
        "yyyy-MM-ddK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "O",
    ];

    /// <summary>Parses an integer using only an optional leading sign.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="input">The text to parse.</param>
    /// <param name="value">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParseInteger<T>(string input, out T value)
        where T : struct, INumberBase<T>
    {
        ArgumentNullException.ThrowIfNull(input);
        return T.TryParse(
            input,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Parses a floating-point number using a sign, decimal point, and exponent.</summary>
    /// <typeparam name="T">The floating-point type.</typeparam>
    /// <param name="input">The text to parse.</param>
    /// <param name="value">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParseFloatingPoint<T>(string input, out T value)
        where T : struct, INumberBase<T>
    {
        ArgumentNullException.ThrowIfNull(input);
        return T.TryParse(
            input,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out value)
            && T.IsFinite(value);
    }

    /// <summary>Parses a decimal number using only a sign and decimal point.</summary>
    /// <param name="input">The text to parse.</param>
    /// <param name="value">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParseDecimal(string input, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(input);
        return decimal.TryParse(
            input,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Parses an ISO date or date-time value.</summary>
    /// <param name="input">The text to parse.</param>
    /// <param name="value">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParseDateTime(string input, out DateTime value)
    {
        ArgumentNullException.ThrowIfNull(input);
        return DateTime.TryParseExact(
            input,
            DateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }

    /// <summary>Parses an ISO date or date-time offset value.</summary>
    /// <param name="input">The text to parse.</param>
    /// <param name="value">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParseDateTimeOffset(string input, out DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(input);
        return DateTimeOffset.TryParseExact(
                input,
                DateTimeOffsetWithoutOffsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out value)
            || DateTimeOffset.TryParseExact(
                input,
                DateTimeOffsetWithOffsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }
}
