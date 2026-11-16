namespace Core.Services;

public interface IMappingService
{
    /// <summary>
    /// Runs one message through the pipeline. Returns how to settle it, or throws when it
    /// should come back later (config unavailable, or a destination status worth retrying).
    /// </summary>
    Task<ProcessResult> ProcessAsync(
        string eventName, string messageId, int deliveryCount, string raw, CancellationToken cancellationToken);
}
