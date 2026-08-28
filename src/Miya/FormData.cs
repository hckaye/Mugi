namespace Miya;

/// <summary>
/// Represents fields and files read from an HTML form request.
/// </summary>
public sealed class FormData
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _fields;
    private readonly IReadOnlyList<FormFile> _files;

    internal FormData(
        IReadOnlyList<KeyValuePair<string, string>> fields,
        IReadOnlyList<FormFile> files)
    {
        _fields = fields;
        _files = files;
    }

    /// <summary>
    /// Gets the form fields in request order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    /// <summary>
    /// Gets the uploaded files in request order.
    /// </summary>
    public IReadOnlyList<FormFile> Files => _files;

    /// <summary>
    /// Gets the first value for a field, or <see langword="null"/> when the field is absent.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The first matching value, or <see langword="null"/>.</returns>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _fields.Count; i++)
        {
            if (string.Equals(_fields[i].Key, name, StringComparison.Ordinal))
            {
                return _fields[i].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets every value for a field in request order.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The matching values.</returns>
    public IReadOnlyList<string> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        List<string>? values = null;
        for (var i = 0; i < _fields.Count; i++)
        {
            if (string.Equals(_fields[i].Key, name, StringComparison.Ordinal))
            {
                values ??= [];
                values.Add(_fields[i].Value);
            }
        }

        return values is null ? Array.Empty<string>() : values;
    }

    /// <summary>
    /// Gets the first uploaded file for a field, or <see langword="null"/> when the field is absent.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The first matching file, or <see langword="null"/>.</returns>
    public FormFile? File(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _files.Count; i++)
        {
            if (string.Equals(_files[i].Name, name, StringComparison.Ordinal))
            {
                return _files[i];
            }
        }

        return null;
    }
}

/// <summary>
/// Represents a file buffered from a multipart form request.
/// </summary>
public sealed class FormFile
{
    internal FormFile(string name, string fileName, string contentType, ReadOnlyMemory<byte> content)
    {
        Name = name;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    /// <summary>
    /// Gets the form field name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the submitted file name without path components.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the submitted content type, or <c>application/octet-stream</c> when none was supplied.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the buffered file content.
    /// </summary>
    public ReadOnlyMemory<byte> Content { get; }
}

/// <summary>
/// Represents an invalid or unsupported form request.
/// </summary>
public sealed class FormException : Exception
{
    internal FormException(string message, int statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets whether the exception was caused by request input.
    /// </summary>
    public bool IsInputError => true;

    /// <summary>
    /// Gets the HTTP status code associated with the error.
    /// </summary>
    public int StatusCode { get; }

    internal static FormException BadRequest(string message, Exception? innerException = null) =>
        new(message, 400, innerException);

    internal static FormException PayloadTooLarge(int limit) =>
        new($"The form body exceeds the configured limit of {limit} bytes.", 413);

    internal static FormException UnsupportedMediaType() =>
        new("The request Content-Type is not a supported form media type.", 415);
}
