namespace Mugi;

public partial class Context
{
    internal int ResponseStatusCode
    {
        get
        {
            EnsureActive();
            return _statusCode;
        }
    }

    internal bool ContainsResponseHeader(string name)
    {
        EnsureActive();
        return _headers.ContainsKey(name);
    }

    internal static void ThrowIfInvalidUserHeader(string name, string value) =>
        ValidateUserHeader(name, value);
}
