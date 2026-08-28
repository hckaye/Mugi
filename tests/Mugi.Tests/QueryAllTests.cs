using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi.Tests;

public sealed class QueryAllTests
{
    [Fact]
    public async Task AbsentKeyReturnsEmptyArray()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("missing");
            return context.Text(values.Length.ToString());
        });

        await using var response = await TestApp.Send(app, queryString: "?name=hello");

        Assert.Equal("0", response.BodyText);
    }

    [Fact]
    public async Task AbsentKeyReturnsEmptyArrayWhenQueryStringIsEmpty()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("name");
            return context.Text(values.Length.ToString());
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("0", response.BodyText);
    }

    [Fact]
    public async Task RepeatedKeysPreserveRequestOrder()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("tag");
            return context.Text(string.Join(",", values));
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?tag=a&tag=b&tag=c");

        Assert.Equal("a,b,c", response.BodyText);
    }

    [Fact]
    public async Task ManyValuesAreAllReturned()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("n");
            return context.Text(values.Length.ToString());
        });

        var expected = 50;
        var query = "?" + string.Join("&", Enumerable.Range(0, expected).Select(i => $"n=v{i}"));
        await using var response = await TestApp.Send(app, queryString: query);

        Assert.Equal(expected.ToString(), response.BodyText);
    }

    [Fact]
    public async Task MixedEmptyValuesArePreserved()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("a");
            return context.Text(
                $"{values.Length}|{values[0].Length}|{values[1]}|{values[2].Length}");
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?a=&a=x&a=");

        Assert.Equal("3|0|x|0", response.BodyText);
    }

    [Fact]
    public async Task BareKeyYieldsSingleEmptyString()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("a");
            return context.Text($"{values.Length}|{values[0].Length}");
        });

        await using var response = await TestApp.Send(app, queryString: "?a");

        Assert.Equal("1|0", response.BodyText);
    }

    [Fact]
    public async Task BareKeyAmongOthersYieldsSingleEmptyString()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("a");
            return context.Text($"{values.Length}|{values[0].Length}");
        });

        await using var response = await TestApp.Send(app, queryString: "?a&b=1");

        Assert.Equal("1|0", response.BodyText);
    }

    [Fact]
    public async Task QueryAllMirrorsQueryForBareKey()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var single = context.Req.Query("a");
            var all = context.Req.QueryAll("a");
            return context.Text($"{single}|{all.Length}|{all[0]}");
        });

        await using var response = await TestApp.Send(app, queryString: "?a");

        Assert.Equal("|1|", response.BodyText);
    }

    [Fact]
    public async Task EncodedPercentValuesAreDecoded()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("v");
            return context.Text(string.Join(",", values));
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?v=a%2Cb&v=c%2Cd");

        Assert.Equal("a,b,c,d", response.BodyText);
    }

    [Fact]
    public async Task PlusIsDecodedAsSpace()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("v");
            return context.Text(string.Join("|", values));
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?v=hello+world&v=foo+bar");

        Assert.Equal("hello world|foo bar", response.BodyText);
    }

    [Fact]
    public async Task UnicodeValuesAreDecoded()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("v");
            return context.Text(string.Join(",", values));
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?v=%E6%97%A5%E6%9C%AC&v=%E3%83%86%E3%82%B9%E3%83%88");

        Assert.Equal("日本,テスト", response.BodyText);
    }

    [Fact]
    public async Task PercentEncodedPercentIsDecodedOnce()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("v");
            return context.Text(values[0]);
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?v=%25FF");

        Assert.Equal("%FF", response.BodyText);
    }

    [Fact]
    public async Task InvalidEscapeThrowsBadRequestLikeQuery()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.QueryAll("v")[0]));

        await using var response = await TestApp.Send(app, queryString: "?v=%FF");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task QueryAndQueryAllThrowSameForInvalidEscape()
    {
        var app = new App();
        app.Get("/", context =>
        {
            _ = context.Req.Query("v");
            _ = context.Req.QueryAll("v");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, queryString: "?v=%FF");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task OnlyMatchingKeyValuesAreReturned()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var values = context.Req.QueryAll("target");
            return context.Text(string.Join(",", values));
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?other=1&target=a&other=2&target=b");

        Assert.Equal("a,b", response.BodyText);
    }

    [Fact]
    public async Task KeyMatchingIsOrdinalCaseSensitive()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var lower = context.Req.QueryAll("key");
            var upper = context.Req.QueryAll("Key");
            return context.Text($"{lower.Length}|{upper.Length}");
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?key=a&Key=b");

        Assert.Equal("1|1", response.BodyText);
    }

    [Fact]
    public async Task NullNameThrowsArgumentNullException()
    {
        var app = new App();
        Exception? observed = null;
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.Req.QueryAll(null!));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentNullException>(observed);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task QueryAllDoesNotAffectQueryFirstValue()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var all = context.Req.QueryAll("a");
            var single = context.Req.Query("a");
            return context.Text($"{single}|{all.Length}");
        });

        await using var response = await TestApp.Send(
            app,
            queryString: "?a=first&a=second");

        Assert.Equal("first|2", response.BodyText);
    }
}
