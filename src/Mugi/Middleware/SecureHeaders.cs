namespace Mugi.Middleware;

/// <summary>
/// Middleware that adds common browser security headers to a buffered response when the handler left them unset.
/// </summary>
public static class SecureHeaders
{
    private const string XContentTypeOptionsName = "X-Content-Type-Options";
    private const string XFrameOptionsName = "X-Frame-Options";
    private const string ReferrerPolicyName = "Referrer-Policy";
    private const string StrictTransportSecurityName = "Strict-Transport-Security";
    private const string XXSSProtectionName = "X-XSS-Protection";
    private const string CrossOriginOpenerPolicyName = "Cross-Origin-Opener-Policy";
    private const string CrossOriginResourcePolicyName = "Cross-Origin-Resource-Policy";
    private const string XPermittedCrossDomainPoliciesName = "X-Permitted-Cross-Domain-Policies";
    private const string XDownloadOptionsName = "X-Download-Options";
    private const string ContentSecurityPolicyName = "Content-Security-Policy";

    /// <summary>
    /// Creates middleware that sets default security headers after next returns, skipping any header the response already has.
    /// Handler values therefore win. Headers are not applied after the response has started streaming.
    /// </summary>
    /// <param name="options">Optional per-header overrides. A null string disables that header.</param>
    /// <returns>Middleware written against <see cref="Context"/>.</returns>
    public static Middleware<Context> Middleware(SecureHeadersOptions? options = null)
    {
        options ??= new SecureHeadersOptions();
        var headers = Snapshot(options);
        return async (context, next) =>
        {
            await next(context).ConfigureAwait(false);
            if (context.ResponseStarted)
            {
                return;
            }

            for (var index = 0; index < headers.Length; index++)
            {
                var header = headers[index];
                if (!context.ContainsResponseHeader(header.Name))
                {
                    context.Header(header.Name, header.Value);
                }
            }
        };
    }

    private static HeaderAssignment[] Snapshot(SecureHeadersOptions options)
    {
        var assigned = new HeaderAssignment[10];
        var count = 0;
        Add(assigned, ref count, XContentTypeOptionsName, options.XContentTypeOptions);
        Add(assigned, ref count, XFrameOptionsName, options.XFrameOptions);
        Add(assigned, ref count, ReferrerPolicyName, options.ReferrerPolicy);
        Add(assigned, ref count, StrictTransportSecurityName, options.StrictTransportSecurity);
        Add(assigned, ref count, XXSSProtectionName, options.XXSSProtection);
        Add(assigned, ref count, CrossOriginOpenerPolicyName, options.CrossOriginOpenerPolicy);
        Add(assigned, ref count, CrossOriginResourcePolicyName, options.CrossOriginResourcePolicy);
        Add(assigned, ref count, XPermittedCrossDomainPoliciesName, options.XPermittedCrossDomainPolicies);
        Add(assigned, ref count, XDownloadOptionsName, options.XDownloadOptions);
        Add(assigned, ref count, ContentSecurityPolicyName, options.ContentSecurityPolicy);

        if (count == assigned.Length)
        {
            return assigned;
        }

        var snapshot = new HeaderAssignment[count];
        Array.Copy(assigned, snapshot, count);
        return snapshot;
    }

    private static void Add(HeaderAssignment[] assigned, ref int count, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        Context.ThrowIfInvalidUserHeader(name, value);
        assigned[count] = new HeaderAssignment(name, value);
        count++;
    }

    private readonly struct HeaderAssignment(string name, string value)
    {
        public string Name { get; } = name;

        public string Value { get; } = value;
    }
}

/// <summary>
/// Options for <see cref="SecureHeaders"/>. A null property disables that header.
/// </summary>
public sealed class SecureHeadersOptions
{
    /// <summary>
    /// Gets the <c>X-Content-Type-Options</c> value. The default is <c>nosniff</c>.
    /// </summary>
    public string? XContentTypeOptions { get; init; } = "nosniff";

    /// <summary>
    /// Gets the <c>X-Frame-Options</c> value. The default is <c>SAMEORIGIN</c>.
    /// </summary>
    public string? XFrameOptions { get; init; } = "SAMEORIGIN";

    /// <summary>
    /// Gets the <c>Referrer-Policy</c> value. The default is <c>no-referrer</c>.
    /// </summary>
    public string? ReferrerPolicy { get; init; } = "no-referrer";

    /// <summary>
    /// Gets the <c>Strict-Transport-Security</c> value. The default is <c>max-age=15552000; includeSubDomains</c>.
    /// </summary>
    public string? StrictTransportSecurity { get; init; } = "max-age=15552000; includeSubDomains";

    /// <summary>
    /// Gets the <c>X-XSS-Protection</c> value. The default is <c>0</c>.
    /// </summary>
    public string? XXSSProtection { get; init; } = "0";

    /// <summary>
    /// Gets the <c>Cross-Origin-Opener-Policy</c> value. The default is <c>same-origin</c>.
    /// </summary>
    public string? CrossOriginOpenerPolicy { get; init; } = "same-origin";

    /// <summary>
    /// Gets the <c>Cross-Origin-Resource-Policy</c> value. The default is <c>same-origin</c>.
    /// </summary>
    public string? CrossOriginResourcePolicy { get; init; } = "same-origin";

    /// <summary>
    /// Gets the <c>X-Permitted-Cross-Domain-Policies</c> value. The default is <c>none</c>.
    /// </summary>
    public string? XPermittedCrossDomainPolicies { get; init; } = "none";

    /// <summary>
    /// Gets the <c>X-Download-Options</c> value. The default is <c>noopen</c>.
    /// </summary>
    public string? XDownloadOptions { get; init; } = "noopen";

    /// <summary>
    /// Gets the <c>Content-Security-Policy</c> value. The default is <see langword="null"/>, which omits the header.
    /// </summary>
    public string? ContentSecurityPolicy { get; init; }
}
