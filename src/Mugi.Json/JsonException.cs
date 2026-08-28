namespace Mugi.Json;

/// <summary>Thrown for invalid JSON input, exceeded limits, or missing codecs.</summary>
public sealed class JsonException : Exception
{
    public JsonException(string message)
        : base(message)
    {
    }

    public JsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JsonException(string message, bool isInputError)
        : base(message)
    {
        IsInputError = isInputError;
    }

    public JsonException(string message, Exception innerException, bool isInputError)
        : base(message, innerException)
    {
        IsInputError = isInputError;
    }

    /// <summary>
    /// Gets whether the exception was caused by invalid JSON syntax or a JSON value that
    /// cannot be represented by the requested target type.
    /// </summary>
    public bool IsInputError { get; }
}
