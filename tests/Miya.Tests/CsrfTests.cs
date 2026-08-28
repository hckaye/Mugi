using System.Text;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class CsrfTests
{
    [Theory]
    [InlineData("GET", StatusCodes.Status200OK)]
    [InlineData("HEAD", StatusCodes.Status200OK)]
    [InlineData("OPTIONS", StatusCodes.Status204NoContent)]
    public async Task SafeMethodsPassWithoutOrigin(string method, int status)
    {
        var app = new App();
        app.Use(Csrf.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: method,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
            });

        Assert.Equal(status, response.Response.StatusCode);
        if (method == "GET")
        {
            Assert.Equal("ok", response.BodyText);
        }
    }

    [Fact]
    public async Task JsonPostPassesWithoutOrigin()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: Encoding.UTF8.GetBytes("{\"n\":1}"),
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task JsonPostWithCharsetAndNullOriginPasses()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "Application/JSON; charset=utf-8",
                ["Origin"] = "null",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task FormPostWithoutOriginReturnsForbidden()
    {
        var called = false;
        var app = CreateFormApp(() => called = true);

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Host"] = "app.example",
            });

        Assert.False(called);
        AssertForbidden(response);
    }

    [Fact]
    public async Task FormPostWithMatchingOriginPasses()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            scheme: "https",
            headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/x-www-form-urlencoded",
                ["origin"] = "https://app.example",
                ["Host"] = "app.example",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
    }

    [Theory]
    [InlineData("https", "http://app.example")]
    [InlineData("http", "https://app.example")]
    public async Task CrossSchemeSameHostOriginIsForbidden(string scheme, string origin)
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            scheme: scheme,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = origin,
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Theory]
    [InlineData("https", "https://app.example")]
    [InlineData("http", "http://app.example")]
    public async Task MatchingSchemeAndHostOriginPasses(string scheme, string origin)
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            scheme: scheme,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = origin,
                ["Host"] = "app.example",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task HostComparisonIsCaseInsensitive()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "http://APP.EXAMPLE:3000",
                ["HOST"] = "app.example:3000",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task MismatchedHostIsForbidden()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "https://evil.example",
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task AllowListAcceptsListedOriginAndIgnoresHost()
    {
        var app = CreateFormApp(options: new CsrfOptions
        {
            Origins = ["https://trusted.example"],
        });

        await using var allowed = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Origin"] = "https://trusted.example",
                ["Host"] = "app.example",
            });
        await using var sameHostNotListed = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Origin"] = "https://app.example",
                ["Host"] = "app.example",
            });

        Assert.Equal(StatusCodes.Status200OK, allowed.Response.StatusCode);
        AssertForbidden(sameHostNotListed);
    }

    [Fact]
    public async Task AllowListDoesNotRequireTheRequestSchemeToMatch()
    {
        var app = CreateFormApp(options: new CsrfOptions
        {
            Origins = ["http://trusted.example"],
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            scheme: "https",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "http://trusted.example",
                ["Host"] = "app.example",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task AllowListMatchingIsCaseSensitive()
    {
        var app = CreateFormApp(options: new CsrfOptions
        {
            Origins = ["https://trusted.example"],
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "multipart/form-data",
                ["Origin"] = "https://TRUSTED.example",
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task ValidateOriginCallbackCanAllow()
    {
        var seen = "";
        var app = CreateFormApp(options: new CsrfOptions
        {
            ValidateOrigin = origin =>
            {
                seen = origin;
                return origin.StartsWith("https://trusted.", StringComparison.Ordinal);
            },
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "https://trusted.example",
                ["Host"] = "app.example",
            });

        Assert.Equal("https://trusted.example", seen);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task ValidateOriginCallbackDoesNotFallBackToSameOrigin()
    {
        var app = CreateFormApp(options: new CsrfOptions
        {
            ValidateOrigin = static _ => false,
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "https://app.example",
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task NullOriginStringIsForbidden()
    {
        var called = false;
        var app = CreateFormApp(() => called = true);

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-www-form-urlencoded",
                ["Origin"] = "null",
                ["Host"] = "app.example",
            });

        Assert.False(called);
        AssertForbidden(response);
    }

    [Fact]
    public async Task MissingContentTypeIsTreatedAsFormLike()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Theory]
    [InlineData("TEXT/PLAIN")]
    [InlineData("multipart/form-data; boundary=abc")]
    [InlineData("application/x-www-form-urlencoded; charset=UTF-8")]
    public async Task FormLikeContentTypesAreChecked(string contentType)
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "PUT",
            path: "/",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = contentType,
                ["Origin"] = "null",
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task PutJsonPasses()
    {
        var app = new App();
        app.Use(Csrf.Middleware());
        app.Put("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "PUT",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task MissingHostInDefaultModeIsForbidden()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "https://app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task NonAbsoluteOriginIsForbidden()
    {
        var app = CreateFormApp();

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "not-a-uri",
                ["Host"] = "app.example",
            });

        AssertForbidden(response);
    }

    [Fact]
    public async Task DefaultMiddlewareOverloadUsesSameOriginMatching()
    {
        var app = new App();
        app.Use(Csrf.Middleware());
        app.Post("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain",
                ["Origin"] = "http://localhost:3000",
                ["Host"] = "localhost:3000",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public void OriginsWithControlCharactersThrowAtFactory()
    {
        Assert.Throws<ArgumentException>(() => Csrf.Middleware(new CsrfOptions
        {
            Origins = ["https://app.example\r"],
        }));
    }

    [Fact]
    public async Task CallbackAndAllowListCombineWithOr()
    {
        var app = CreateFormApp(options: new CsrfOptions
        {
            Origins = ["https://listed.example"],
            ValidateOrigin = static origin => origin == "https://callback.example",
        });

        await using var listed = await TestApp.Send(
            app,
            method: "POST",
            headers: FormHeaders("https://listed.example"));
        await using var callback = await TestApp.Send(
            app,
            method: "POST",
            headers: FormHeaders("https://callback.example"));
        await using var neither = await TestApp.Send(
            app,
            method: "POST",
            headers: FormHeaders("https://app.example"));

        Assert.Equal(StatusCodes.Status200OK, listed.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, callback.Response.StatusCode);
        AssertForbidden(neither);
    }

    private static App CreateFormApp(Action? onHandler = null, CsrfOptions? options = null)
    {
        var app = new App();
        app.Use(Csrf.Middleware(options));
        app.Post("/", context =>
        {
            onHandler?.Invoke();
            return context.Text("ok");
        });
        app.Put("/", context =>
        {
            onHandler?.Invoke();
            return context.Text("ok");
        });
        return app;
    }

    private static Dictionary<string, string> FormHeaders(string origin) => new()
    {
        ["Content-Type"] = "application/x-www-form-urlencoded",
        ["Origin"] = origin,
        ["Host"] = "app.example",
    };

    private static void AssertForbidden(TestExchange response)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
        Assert.Equal("Forbidden", response.BodyText);
    }
}
