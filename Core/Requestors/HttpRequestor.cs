using System.Net;
using System.Text;

namespace Core.Requestors;

/// <summary>What came back from the destination. Body is only read on failure.</summary>
public sealed record HttpResult(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

public class HttpRequestor(IHttpClientFactory httpClientFactory) : IHttpRequestor
{
    /// <summary>
    /// POSTs a JSON payload. The idempotency key is sent as a header so the destination can
    /// dedupe the redeliveries Service Bus makes on any failure.
    /// </summary>
    public async Task<HttpResult> PostJsonAsync(
        Uri url, string json, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);

        // A success body is never used, so don't pay to read one that might be large.
        var body = response.IsSuccessStatusCode
            ? ""
            : await response.Content.ReadAsStringAsync(cancellationToken);

        return new(response.StatusCode, body);
    }

    /// <summary>
    /// True when replaying the same request could plausibly succeed. 408 and 429 are 4xx but
    /// explicitly mean "try again"; every other 4xx is the destination rejecting this payload.
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
