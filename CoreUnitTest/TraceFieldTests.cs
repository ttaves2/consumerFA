using Core.Services;

namespace CoreUnitTest;

public class TraceFieldTests
{
    private static Dictionary<string, object?> Extract(string json) =>
        MappingService.ExtractTraceFields(json).ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("""{"orderId":"A1"}""", "orderId")]
    [InlineData("""{"orderID":"A1"}""", "orderID")]
    [InlineData("""{"ORDER_ID":"A1"}""", "ORDER_ID")]
    [InlineData("""{"order_id":"A1"}""", "order_id")]
    [InlineData("""{"order-id":"A1"}""", "order-id")]
    [InlineData("""{"id":"A1"}""", "id")]
    [InlineData("""{"ID":"A1"}""", "ID")]
    [InlineData("""{"accountNumber":"A1"}""", "accountNumber")]
    [InlineData("""{"account_number":"A1"}""", "account_number")]
    [InlineData("""{"number":"A1"}""", "number")]
    [InlineData("""{"@orderId":"A1"}""", "@orderId")]  // XML attribute
    public void Matches(string json, string expectedKey) =>
        Assert.Equal("A1", Extract(json)[expectedKey]);

    [Theory]
    // The whole point of requiring a boundary: these all end in the letters "id".
    [InlineData("""{"isPaid":true}""")]
    [InlineData("""{"isValid":false}""")]
    [InlineData("""{"unpaid":"1"}""")]
    [InlineData("""{"prepaid":"1"}""")]
    [InlineData("""{"hybrid":"1"}""")]
    [InlineData("""{"avoid":"1"}""")]
    // All-lowercase suffixes are deliberately not matched, so this is a miss, not a bug.
    [InlineData("""{"orderid":"A1"}""")]
    [InlineData("""{"phonenumber":"555"}""")]
    public void DoesNotMatch(string json) =>
        Assert.Empty(MappingService.ExtractTraceFields(json));

    [Fact]
    public void FindsFieldsNestedAtAnyDepth()
    {
        var fields = Extract("""
            {"envelope":{"header":{"correlationId":"C1"},"payload":{"order":{"orderId":"O1"}}}}
            """);

        Assert.Equal("C1", fields["correlationId"]);
        Assert.Equal("O1", fields["orderId"]);
    }

    [Fact]
    public void CollapsesRepeatedNamesFromArraysIntoOneDimension()
    {
        var fields = Extract("""
            {"lines":[{"lineId":"L1"},{"lineId":"L2"},{"lineId":"L1"}]}
            """);

        // One dimension, distinct values, rather than three dimensions all called lineId.
        Assert.Equal("L1,L2", Assert.Single(fields).Value);
    }

    [Fact]
    public void SkipsNonScalarValues() =>
        Assert.Empty(MappingService.ExtractTraceFields("""{"id":{"nested":"A1"}}"""));

    [Fact]
    public void SkipsNamesItAlreadyLogs()
    {
        // A body field called messageId must not shadow the Service Bus MessageId dimension.
        var fields = Extract("""{"messageId":"spoofed","orderId":"O1"}""");

        Assert.Equal("O1", Assert.Single(fields).Value);
    }

    [Fact]
    public void StopsAtTheFieldCap()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"f{i}Id\":\"v{i}\"")) + "}";

        Assert.Equal(10, MappingService.ExtractTraceFields(json).Count);
    }

    [Fact]
    public void TruncatesLongValues()
    {
        var json = $"{{\"orderId\":\"{new string('x', 500)}\"}}";

        var value = (string)Assert.Single(MappingService.ExtractTraceFields(json)).Value!;

        Assert.StartsWith(new string('x', 200), value);
        Assert.EndsWith("...[truncated]", value);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{broken")]
    public void ReturnsNothingRatherThanThrowingOnUnparseableInput(string json) =>
        Assert.Empty(MappingService.ExtractTraceFields(json));

    [Fact]
    public void KeepsNumericAndBooleanValues()
    {
        var fields = Extract("""{"orderId":10044,"lineNumber":3}""");

        Assert.Equal("10044", fields["orderId"]);
        Assert.Equal("3", fields["lineNumber"]);
    }

    [Fact]
    public void ExtractsFromConvertedXml()
    {
        var fields = Extract(MappingService.ToJson(
            """<order OrderId="O1"><customerNumber>C2</customerNumber></order>"""));

        Assert.Equal("O1", fields["@OrderId"]);
        Assert.Equal("C2", fields["customerNumber"]);
    }
}
