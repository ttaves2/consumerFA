namespace Core.Requestors;

public interface IHttpRequestor
{
    /// <summary>POSTs a JSON payload, sending the idempotency key as a header.</summary>
    Task<HttpResult> PostJsonAsync(Uri url, string json, string idempotencyKey, CancellationToken cancellationToken);
}
