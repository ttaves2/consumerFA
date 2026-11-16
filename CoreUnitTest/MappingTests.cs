using System.Text.Json.Nodes;
using Core.Services;
using Jsonata.Net.Native;

namespace CoreUnitTest;

public class MappingTests
{
    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"expected {expected}, got {actual}");

    [Fact]
    public void Map_RenamesAndAggregates()
    {
        var query = new JsonataQuery("""{"id": orderId, "total": $sum(lines.amount)}""");

        var result = MappingService.Map(query, """{"orderId":"A1","lines":[{"amount":2},{"amount":3}]}""");

        AssertJsonEqual("""{"id":"A1","total":5}""", result);
    }

    [Fact]
    public void Map_OmitsFieldsMissingFromTheMessage()
    {
        var query = new JsonataQuery("""{"id": orderId, "note": missingField}""");

        var result = MappingService.Map(query, """{"orderId":"A1"}""");

        AssertJsonEqual("""{"id":"A1"}""", result);
    }

    [Fact]
    public void Map_ThrowsOnMalformedMessage()
    {
        var query = new JsonataQuery("""{"id": orderId}""");

        Assert.ThrowsAny<Exception>(() => MappingService.Map(query, "not json"));
    }

    [Fact]
    public void BadMappingConfig_ThrowsWhenCompiled()
    {
        Assert.ThrowsAny<Exception>(() => new JsonataQuery("{ this is not jsonata"));
    }
}
