namespace Miya.Json;

/// <summary>Thrown for invalid JSON input, exceeded limits, or missing codecs.</summary>
public sealed class MiyaJsonException : Exception
{
    public MiyaJsonException(string message)
        : base(message)
    {
    }

    public MiyaJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MiyaJsonException(string message, bool isInputError)
        : base(message)
    {
        IsInputError = isInputError;
    }

    public MiyaJsonException(string message, Exception innerException, bool isInputError)
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
