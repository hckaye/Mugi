using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class HtmlInterpolationTests
{
    [Fact]
    public async Task LiteralOnlyIsByteExactPassthrough()
    {
        // A runtime hole keeps overload resolution on the interpolated-string handler.
        var empty = string.Empty;
        await using var response = await SendAsync(context => context.Html($"<h1>Hello</h1>{empty}"));

        Assert.Equal(Encoding.UTF8.GetBytes("<h1>Hello</h1>"), BodyBytes(response));
        Assert.Equal("<h1>Hello</h1>", response.BodyText);
    }

    [Theory]
    [InlineData("&", "&amp;")]
    [InlineData("<", "&lt;")]
    [InlineData(">", "&gt;")]
    [InlineData("\"", "&quot;")]
    [InlineData("'", "&#39;")]
    public async Task EachEscapableCharacterIsReplaced(string value, string escaped)
    {
        await using var response = await SendAsync(context => context.Html($"{value}"));

        Assert.Equal(escaped, response.BodyText);
    }

    [Fact]
    public async Task ConsecutiveAndBoundarySpecialCharactersAreEscaped()
    {
        var value = "&<>\"'";
        await using var response = await SendAsync(context => context.Html($"[{value}]"));

        Assert.Equal("[&amp;&lt;&gt;&quot;&#39;]", response.BodyText);
    }

    [Fact]
    public async Task MixedLiteralsAndValuesEscapeOnlyTheHoles()
    {
        var name = "Ada & Bob";
        var score = 12;
        await using var response = await SendAsync(
            context => context.Html($"<p>{name} scored {score} points</p>"));

        Assert.Equal("<p>Ada &amp; Bob scored 12 points</p>", response.BodyText);
    }

    [Fact]
    public async Task NullStringValueWritesNothing()
    {
        string? missing = null;
        await using var response = await SendAsync(context => context.Html($"a{missing}b"));

        Assert.Equal("ab", response.BodyText);
    }

    [Fact]
    public async Task RawHtmlIsWrittenVerbatim()
    {
        var markup = RawHtml.From("<b>bold & italics</b>");
        var name = "<script>";
        await using var response = await SendAsync(
            context => context.Html($"<div>{markup} {name}</div>"));

        Assert.Equal("<div><b>bold & italics</b> &lt;script&gt;</div>", response.BodyText);
    }

    [Fact]
    public void RawHtmlFromNullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => RawHtml.From(null!));
    }

    [Fact]
    public async Task SpanFormattableValuesUseInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var commaCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = commaCulture;
        CultureInfo.CurrentUICulture = commaCulture;
        try
        {
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            var date = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            var number = 1234.5;
            var integer = 42;
            await using var numbers = await SendAsync(context => context.Html($"{number}"));
            await using var formattedNumbers = await SendAsync(context => context.Html($"{number:F1}"));
            await using var integers = await SendAsync(context => context.Html($"{integer}"));
            await using var formattedIntegers = await SendAsync(context => context.Html($"{integer:D5}"));
            await using var dates = await SendAsync(context => context.Html($"{date}"));
            await using var formattedDates = await SendAsync(context => context.Html($"{date:yyyy-MM-dd}"));

            Assert.Equal("1234.5", numbers.BodyText);
            Assert.Equal("1234.5", formattedNumbers.BodyText);
            Assert.Equal("42", integers.BodyText);
            Assert.Equal("00042", formattedIntegers.BodyText);
            Assert.Equal(date.ToString(CultureInfo.InvariantCulture), dates.BodyText);
            Assert.Equal("2024-01-02", formattedDates.BodyText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task LongSpanFormattableValuesGrowPastTheStackBufferAndAreEscaped()
    {
        var value = new LongSpanFormattable(300, '<');
        await using var response = await SendAsync(context => context.Html($"[{value}]"));

        Assert.Equal("[" + new string('<', 300).Replace("<", "&lt;", StringComparison.Ordinal) + "]", response.BodyText);
    }

    [Fact]
    public async Task VeryLongSpanFormattableValuesGrowThePooledCharBuffer()
    {
        var value = new LongSpanFormattable(10_000, 'a');
        await using var response = await SendAsync(context => context.Html($"{value}"));

        Assert.Equal(new string('a', 10_000), response.BodyText);
    }

    [Fact]
    public async Task XssPayloadsAreNeutralized()
    {
        var script = "<script>alert(1)</script>";
        var attributeBreak = "\" onmouseover=\"alert(1)";
        var javascriptHref = "javascript:alert(1)";
        var javascriptBreak = "javascript:alert(1)\" onclick=\"alert(1)";

        await using var scriptResponse = await SendAsync(
            context => context.Html($"<p>{script}</p>"));
        await using var attributeResponse = await SendAsync(
            context => context.Html($"<img alt=\"{attributeBreak}\">"));
        await using var javascriptTextResponse = await SendAsync(
            context => context.Html($"<p>{javascriptHref}</p>"));
        await using var javascriptHrefResponse = await SendAsync(
            context => context.Html($"<a href=\"{javascriptBreak}\">x</a>"));

        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", scriptResponse.BodyText);
        Assert.DoesNotContain("<script", scriptResponse.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<img alt=\"&quot; onmouseover=&quot;alert(1)\">", attributeResponse.BodyText);
        Assert.DoesNotContain("onmouseover=\"", attributeResponse.BodyText, StringComparison.Ordinal);
        Assert.Equal("<p>javascript:alert(1)</p>", javascriptTextResponse.BodyText);
        Assert.Equal(
            "<a href=\"javascript:alert(1)&quot; onclick=&quot;alert(1)\">x</a>",
            javascriptHrefResponse.BodyText);
        Assert.DoesNotContain("onclick=\"", javascriptHrefResponse.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeValuesRoundTrip()
    {
        var value = "日本語😀 & 中文";
        await using var response = await SendAsync(context => context.Html($"<p>{value}</p>"));

        var expected = "<p>日本語😀 &amp; 中文</p>";
        Assert.Equal(expected, response.BodyText);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), BodyBytes(response));
    }

    [Fact]
    public async Task OneMebibyteInterpolatedValueGrowsThePooledBuffer()
    {
        var value = new string('a', 1024 * 1024);
        await using var response = await SendAsync(context => context.Html($"pre{value}post"));

        Assert.Equal("pre" + value + "post", response.BodyText);
    }

    [Fact]
    public async Task StringVariablesStayRawWhileInterpolatedHolesAreEscaped()
    {
        var markup = "<b>";
        await using var raw = await SendAsync(context => context.Html(markup));
        await using var escaped = await SendAsync(context => context.Html($"{markup}"));

        Assert.Equal("<b>", raw.BodyText);
        Assert.Equal("&lt;b&gt;", escaped.BodyText);
    }

    [Fact]
    public async Task ResponseHeadersAndStatusMatchHtmlString()
    {
        await using var fromString = await SendAsync(context =>
        {
            context.Status(201);
            context.Header("X-Test", "ok");
            return context.Html("<p>ok</p>");
        });
        await using var fromHandler = await SendAsync(context =>
        {
            context.Status(201);
            context.Header("X-Test", "ok");
            var empty = string.Empty;
            return context.Html($"<p>ok</p>{empty}");
        });

        Assert.Equal(fromString.Response.StatusCode, fromHandler.Response.StatusCode);
        Assert.Equal(
            fromString.Response.Headers["Content-Type"].ToString(),
            fromHandler.Response.Headers["Content-Type"].ToString());
        Assert.Equal("text/html; charset=utf-8", fromHandler.Response.Headers["Content-Type"].ToString());
        Assert.Equal(
            fromString.Response.Headers["Content-Length"].ToString(),
            fromHandler.Response.Headers["Content-Length"].ToString());
        Assert.Equal(
            fromString.Response.Headers["X-Test"].ToString(),
            fromHandler.Response.Headers["X-Test"].ToString());
        Assert.Equal(fromString.BodyText, fromHandler.BodyText);
        Assert.Equal(StatusCodes.Status201Created, fromHandler.Response.StatusCode);
    }

    [Fact]
    public async Task HeadSuppressesTheBodyAndKeepsTheMeasuredContentLength()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var name = "Ada";
            return context.Html($"<h1>{name}</h1>");
        });

        await using var get = await TestApp.Send(app);
        await using var head = await TestApp.Send(app, method: "HEAD");

        Assert.Equal("<h1>Ada</h1>", get.BodyText);
        Assert.Empty(head.BodyText);
        Assert.Equal(get.Response.Headers["Content-Type"].ToString(), head.Response.Headers["Content-Type"].ToString());
        Assert.Equal(get.Response.Headers["Content-Length"].ToString(), head.Response.Headers["Content-Length"].ToString());
        Assert.Equal("12", head.Response.Headers["Content-Length"].ToString());
    }

    [Fact]
    public async Task WorksInsideCustomContextHandlers()
    {
        var app = new App<CustomHtmlContext>();
        app.Get("/", context => context.Html($"<h1>{context.Label}</h1>"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("<h1>Ada &amp; Grace</h1>", response.BodyText);
        Assert.Equal("text/html; charset=utf-8", response.Response.Headers["Content-Type"].ToString());
    }

    [Fact]
    public async Task SpentHandlerCannotBeWrittenOrAppendedAgain()
    {
        InvalidOperationException? secondWrite = null;
        InvalidOperationException? appendAfterUse = null;
        var app = new App();
        app.Get("/", context =>
        {
            var handler = new HtmlInterpolatedStringHandler(5, 0, context);
            handler.AppendLiteral("hello");
            var first = context.Html(ref handler);
            try
            {
                _ = context.Html(ref handler);
            }
            catch (InvalidOperationException exception)
            {
                secondWrite = exception;
            }

            try
            {
                handler.AppendLiteral("x");
            }
            catch (InvalidOperationException exception)
            {
                appendAfterUse = exception;
            }

            return first;
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("hello", response.BodyText);
        Assert.NotNull(secondWrite);
        Assert.NotNull(appendAfterUse);
    }

    [Fact]
    public async Task HandlerBufferIsReturnedWhenSendingFails()
    {
        InvalidOperationException? first = null;
        InvalidOperationException? second = null;
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Stream(
                "text/plain",
                static (writer, _) =>
                {
                    writer.Write("sent"u8);
                    return ValueTask.CompletedTask;
                });

            var handler = new HtmlInterpolatedStringHandler(2, 0, context);
            handler.AppendLiteral("hi");
            try
            {
                _ = context.Html(ref handler);
            }
            catch (InvalidOperationException exception)
            {
                first = exception;
            }

            try
            {
                _ = context.Html(ref handler);
            }
            catch (InvalidOperationException exception)
            {
                second = exception;
            }
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("sent", response.BodyText);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(
            "The HTML interpolated string handler has already been used.",
            second.Message);
    }

    [Fact]
    public async Task EmptyInterpolatedStringWritesAnEmptyHtmlBody()
    {
        await using var response = await SendAsync(context => context.Html($""));

        Assert.Empty(response.BodyText);
        Assert.Equal("text/html; charset=utf-8", response.Response.Headers["Content-Type"].ToString());
        Assert.Equal("0", response.Response.Headers["Content-Length"].ToString());
    }

    [Fact]
    public async Task AmpersandAlreadyInAnEntityIsEscapedWhenInterpolated()
    {
        var value = "&amp;";
        await using var response = await SendAsync(context => context.Html($"{value}"));

        Assert.Equal("&amp;amp;", response.BodyText);
    }

    [Fact]
    public async Task LiteralEntitiesAreNotReEscaped()
    {
        await using var response = await SendAsync(context => context.Html($"&amp;<b>"));

        Assert.Equal("&amp;<b>", response.BodyText);
    }

    private static async Task<TestExchange> SendAsync(Handler<Context> handler)
    {
        var app = new App();
        app.Get("/", handler);
        return await TestApp.Send(app);
    }

    private static byte[] BodyBytes(TestExchange response) => response.ResponseBody.Body.ToArray();

    public sealed class CustomHtmlContext : Context
    {
        public string Label { get; } = "Ada & Grace";
    }

    private readonly struct LongSpanFormattable : ISpanFormattable
    {
        private readonly int _length;
        private readonly char _character;

        public LongSpanFormattable(int length, char character)
        {
            _length = length;
            _character = character;
        }

        public string ToString(string? format, IFormatProvider? formatProvider) =>
            new(_character, _length);

        public bool TryFormat(
            Span<char> destination,
            out int charsWritten,
            ReadOnlySpan<char> format,
            IFormatProvider? provider)
        {
            if (destination.Length < _length)
            {
                charsWritten = 0;
                return false;
            }

            destination[.._length].Fill(_character);
            charsWritten = _length;
            return true;
        }
    }
}
