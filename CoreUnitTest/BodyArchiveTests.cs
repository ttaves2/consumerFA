using Azure.Storage.Blobs;
using Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoreUnitTest;

public class BodyArchiveTests
{
    private const int OverTheLimit = 5000;   // MaxBodyChars is 4000

    /// <summary>A client that cannot reach anything and gives up immediately rather than retrying.</summary>
    private static BlobContainerClient Unreachable() =>
        new(
            "DefaultEndpointsProtocol=http;AccountName=dev;AccountKey=Zm9v;BlobEndpoint=http://127.0.0.1:1/dev;",
            "message-bodies",
            new BlobClientOptions { Retry = { MaxRetries = 0 } });

    private static Task<string> BodyForLog(BlobContainerClient? archive, string raw) =>
        MyFirstQueueFunction.BodyForLogAsync(archive, raw, "sb-1", NullLogger.Instance, default);

    [Fact]
    public async Task ShortBodiesAreLoggedWholeAndNeverTouchStorage()
    {
        var raw = new string('x', 100);

        // Passing a client that would fail proves nothing was uploaded: it returns before trying.
        Assert.Equal(raw, await BodyForLog(Unreachable(), raw));
    }

    [Fact]
    public async Task LongBodiesAreTruncatedWhenThereIsNowhereToArchiveThem()
    {
        var result = await BodyForLog(null, new string('x', OverTheLimit));

        Assert.EndsWith("...[truncated]", result);
        Assert.DoesNotContain("full body", result);
    }

    [Fact]
    public async Task AStorageFailureCostsTheLinkButNotTheMessage()
    {
        // The whole point of the catch: a storage outage must not dead-letter working payloads.
        var result = await BodyForLog(Unreachable(), new string('x', OverTheLimit));

        Assert.EndsWith("...[truncated]", result);
        Assert.DoesNotContain("full body", result);
    }

    [Fact]
    public void AConnectionStringWins()
    {
        var client = MyFirstQueueFunction.ArchiveClient(
            "DefaultEndpointsProtocol=http;AccountName=dev;AccountKey=Zm9v;BlobEndpoint=http://127.0.0.1:1/dev;",
            blobServiceUri: "https://ignored.blob.core.windows.net",
            accountName: "ignored");

        Assert.Equal("dev", client!.AccountName);
    }

    [Fact]
    public void AnAccountNameAloneIsEnough()
    {
        // The deployed form: AzureWebJobsStorage__accountName with no connection string anywhere.
        var client = MyFirstQueueFunction.ArchiveClient(null, null, accountName: "prodstorage");

        Assert.Equal(
            "https://prodstorage.blob.core.windows.net/message-bodies", client!.Uri.ToString());
    }

    [Fact]
    public void AServiceUriBeatsAnAccountName() =>
        // Deriving the host from the account name hardcodes the public cloud's suffix.
        Assert.Equal(
            "https://prodstorage.blob.core.chinacloudapi.cn/message-bodies",
            MyFirstQueueFunction.ArchiveClient(
                null,
                "https://prodstorage.blob.core.chinacloudapi.cn/",
                "prodstorage")!.Uri.ToString());

    [Fact]
    public void NoStorageSettingAtAllDisablesArchiving() =>
        Assert.Null(MyFirstQueueFunction.ArchiveClient(null, null, null));
}
