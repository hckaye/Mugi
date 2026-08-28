namespace Mugi.Middleware;

/// <summary>
/// A context that stores the request identifier assigned by <see cref="RequestId"/>.
/// </summary>
public interface IRequestIdContext
{
    /// <summary>
    /// Gets or sets the identifier for the current request.
    /// </summary>
    string? RequestId { get; set; }
}
