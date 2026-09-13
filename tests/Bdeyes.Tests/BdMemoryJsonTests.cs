using Bdeyes.Services;

namespace Bdeyes.Tests;

public sealed class BdMemoryJsonTests
{
    [Fact]
    public void CatalogParsesExactValuesAndExcludesSchemaMetadata()
    {
        const string json = """
            {
              "z-last": "second line\nkept exactly",
              "schema_version": 1,
              "a-first": "first value"
            }
            """;

        var memories = BdClient.ParseMemories(json);

        Assert.Equal(2, memories.Count);
        Assert.DoesNotContain(memories, memory => memory.Key == "schema_version");
        Assert.Equal(
            "first value",
            memories.Single(memory => memory.Key == "a-first").Value);
        Assert.Equal(
            "second line\nkept exactly",
            memories.Single(memory => memory.Key == "z-last").Value);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":1,\"broken\":42}")]
    [InlineData("not json")]
    public void UnexpectedCatalogShapeFailsClearly(string json)
    {
        Assert.Throws<BdClientException>(() => BdClient.ParseMemories(json));
    }
}
