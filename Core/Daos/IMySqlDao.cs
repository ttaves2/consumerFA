namespace Core.Daos;

public interface IMySqlDao
{
    /// <summary>The newest InboundMappingConfig row for the event. Throws if none exists.</summary>
    Task<InboundMappingConfig> GetInboundMappingConfigAsync(string eventName, CancellationToken cancellationToken);
}
