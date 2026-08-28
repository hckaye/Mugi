global using Miya.Testing;

using Microsoft.AspNetCore.Http.Features;

namespace Miya.Tests;

internal static class TestApp
{
    public static async Task<TestExchange> Send<TContext>(
        App<TContext> app,
        string method = "GET",
        string path = "/",
        string queryString = "",
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        AppOptions? options = null,
        string? rawTarget = null,
        string? scheme = null,
        string? protocol = null,
        IHttpConnectionFeature? connection = null,
        bool? upgradable = null,
        string? extendedConnectProtocol = null)
        where TContext : Context, new()
    {
        var exchange = TestExchange.Create(
            method,
            path,
            queryString,
            body,
            headers,
            rawTarget,
            scheme,
            protocol,
            connection,
            upgradable,
            extendedConnectProtocol);
        try
        {
            await app.ExecuteAsync(exchange.Features, options);
            return exchange;
        }
        catch
        {
            await exchange.DisposeAsync();
            throw;
        }
    }
}
