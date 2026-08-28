using Mugi.Generators.Core;

namespace Mugi.Generators.Tests;

public sealed class SimpleJsonParserTests
{
    [Fact]
    public void Parses_json_strings_escapes_numbers_and_literals()
    {
        const string json = """
            {
              "text": "quote: \" slash: \\ solidus: \/ controls: \b\f\n\r\t unicode: \u65e5\ud83d\ude00",
              "numbers": [-12, 0, 1.25, 6.02e23],
              "true": true,
              "false": false,
              "null": null
            }
            """;

        Assert.True(SimpleJsonParser.TryParse(json, out var value, out var error), error?.Message);
        var root = Assert.IsType<SimpleJsonObject>(value);
        Assert.True(root.TryGetValue("text", out var textValue));
        Assert.Equal(
            "quote: \" slash: \\ solidus: / controls: \b\f\n\r\t unicode: 日😀",
            Assert.IsType<SimpleJsonString>(textValue).Value);
        Assert.True(root.TryGetValue("numbers", out var numbersValue));
        var numbers = Assert.IsType<SimpleJsonArray>(numbersValue);
        Assert.Equal("-12", Assert.IsType<SimpleJsonNumber>(numbers.Items[0]).Text);
        Assert.Equal("0", Assert.IsType<SimpleJsonNumber>(numbers.Items[1]).Text);
        Assert.Equal("1.25", Assert.IsType<SimpleJsonNumber>(numbers.Items[2]).Text);
        Assert.Equal("6.02e23", Assert.IsType<SimpleJsonNumber>(numbers.Items[3]).Text);
        Assert.IsType<SimpleJsonBoolean>(root.Properties.Single(property => property.Name == "true").Value);
        Assert.IsType<SimpleJsonNull>(root.Properties.Single(property => property.Name == "null").Value);
    }

    [Theory]
    [InlineData("{\"value\": 01}")]
    [InlineData("{\"value\": 1.}")]
    [InlineData("{\"value\": 1e}")]
    [InlineData("{\"value\": \"\\ud800\"}")]
    [InlineData("{\"value\": \"\\udc00\"}")]
    [InlineData("{\"value\": 1} trailing")]
    public void Rejects_invalid_json_tokens(string json)
    {
        Assert.False(SimpleJsonParser.TryParse(json, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }
}
