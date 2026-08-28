namespace Mugi.Middleware;

/// <summary>
/// A derived <see cref="Context"/> that can store the identity established by authentication middleware.
/// </summary>
public interface IAuthContext
{
    /// <summary>
    /// Gets or sets the authenticated user name (Basic) or the validated bearer token string (Bearer).
    /// </summary>
    string? AuthUser { get; set; }
}
