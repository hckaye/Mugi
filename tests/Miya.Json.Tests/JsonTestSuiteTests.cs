namespace Miya.Json.Tests;

public sealed class JsonTestSuiteTests
{
    [Theory]
    [MemberData(nameof(AcceptedDocuments))]
    public void RequiredAcceptedDocumentsParse(string path)
    {
        var json = File.ReadAllBytes(path);
        Assert.True(Json.Deserialize(json, SkipCodec.Instance));
    }

    [Theory]
    [MemberData(nameof(RejectedDocuments))]
    public void RequiredRejectedDocumentsFail(string path)
    {
        var json = File.ReadAllBytes(path);
        Assert.Throws<JsonException>(() => Json.Deserialize(json, SkipCodec.Instance));
    }

    public static TheoryData<string> AcceptedDocuments => Load("y_*.json");

    public static TheoryData<string> RejectedDocuments => Load("n_*.json");

    private static TheoryData<string> Load(string pattern)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestData", "JSONTestSuite");
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(directory, pattern).Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }
}
