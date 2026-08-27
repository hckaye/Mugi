namespace Miya.Tests;

public sealed class HeaderTests
{
    [Theory]
    [InlineData("Bad Header", "value")]
    [InlineData("X-Test", "one\r\ntwo")]
    [InlineData("X-Test", "one\0two")]
    public async Task InvalidHeadersAreRejectedWhenSet(string name, string value)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.Header(name, value));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentException>(observed);
        Assert.Equal("ok", response.BodyText);
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("content-length")]
    public async Task FrameworkManagedHeadersAreRejected(string name)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.Header(name, "1"));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<InvalidOperationException>(observed);
    }

    [Fact]
    public async Task RedirectValidatesLocationHeader()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.Redirect("/safe\r\nInjected: yes"));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentException>(observed);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task AppendHeaderPreservesSeparateValues()
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.AppendHeader("Set-Cookie", "a=1");
            context.AppendHeader("Set-Cookie", "b=2");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        var cookies = response.Response.Headers.SetCookie;
        Assert.Equal(2, cookies.Count);
        Assert.Equal("a=1", cookies[0]!);
        Assert.Equal("b=2", cookies[1]!);
    }
}
