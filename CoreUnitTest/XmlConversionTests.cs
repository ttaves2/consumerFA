using System.Text.Json.Nodes;
using Core;
using Jsonata.Net.Native;

namespace CoreUnitTest;

public class XmlConversionTests
{
    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"expected {expected}, got {actual}");

    [Theory]
    [InlineData("""{"orderId":"A1"}""")]
    [InlineData("""   {"orderId":"A1"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("\"just a string\"")]
    public void ToJson_PassesJsonThrough(string body) =>
        Assert.Equal(body, MyFirstQueueFunction.ToJson(body));

    [Fact]
    public void ToJson_KeepsTheRootElement() =>
        AssertJsonEqual(
            """{"order":{"id":"1"}}""",
            MyFirstQueueFunction.ToJson("<order><id>1</id></order>"));

    [Fact]
    public void ToJson_PrefixesAttributesWithAt() =>
        AssertJsonEqual(
            """{"order":{"@id":"1","note":"hi"}}""",
            MyFirstQueueFunction.ToJson("""<order id="1"><note>hi</note></order>"""));

    [Fact]
    public void ToJson_TurnsRepeatedElementsIntoAnArray() =>
        AssertJsonEqual(
            """{"lines":{"line":["a","b"]}}""",
            MyFirstQueueFunction.ToJson("<lines><line>a</line><line>b</line></lines>"));

    [Fact]
    public void ToJson_LeavesASingleRepeatedElementScalar() =>
        AssertJsonEqual(
            """{"lines":{"line":"a"}}""",
            MyFirstQueueFunction.ToJson("<lines><line>a</line></lines>"));

    [Theory]
    [InlineData("\uFEFF<order><id>1</id></order>")]          // byte order mark
    [InlineData("  \r\n\t<order><id>1</id></order>")]         // leading whitespace
    public void ToJson_DetectsXmlBehindLeadingNoise(string body) =>
        Assert.Contains("\"order\"", MyFirstQueueFunction.ToJson(body));

    [Fact]
    public void ToJson_ThrowsOnMalformedXml() =>
        Assert.ThrowsAny<Exception>(() => MyFirstQueueFunction.ToJson("<order><id>1</id>"));

    [Fact]
    public void ToJson_RejectsDoctypeSoXxeCannotReadFiles()
    {
        // Without DtdProcessing.Prohibit this resolves &xxe; and leaks the file into the payload.
        var attack = """
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
            <order><id>&xxe;</id></order>
            """;

        Assert.ThrowsAny<Exception>(() => MyFirstQueueFunction.ToJson(attack));
    }

    [Fact]
    public void ToJson_RejectsEntityExpansionBomb()
    {
        // "Billion laughs": prohibited DTDs stop this before it expands.
        var bomb = """
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            ]>
            <lolz>&lol3;</lolz>
            """;

        Assert.ThrowsAny<Exception>(() => MyFirstQueueFunction.ToJson(bomb));
    }

    [Fact]
    public void XmlFlowsThroughJsonataEndToEnd()
    {
        // XML values are always strings, so arithmetic in a mapping needs $number().
        var query = new JsonataQuery(
            """{"id": order.id, "total": $sum(order.lines.line.amount.$number($))}""");

        var mapped = MyFirstQueueFunction.Map(query, MyFirstQueueFunction.ToJson(
            """
            <order><id>A1</id><lines>
              <line><amount>2</amount></line>
              <line><amount>3</amount></line>
            </lines></order>
            """));

        AssertJsonEqual("""{"id":"A1","total":5}""", mapped);
    }

    [Fact]
    public void ToJson_KeepsTheDeclarationAsAProperty() =>
        // Worth knowing when writing a mapping: the declaration shows up as "?xml", though it
        // sits beside the root rather than wrapping it, so order.id is unaffected.
        AssertJsonEqual(
            """{"?xml":{"@version":"1.0","@encoding":"utf-8"},"order":{"id":"1"}}""",
            MyFirstQueueFunction.ToJson(
                """<?xml version="1.0" encoding="utf-8"?><order><id>1</id></order>"""));
}
