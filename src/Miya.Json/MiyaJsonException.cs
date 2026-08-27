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
}
