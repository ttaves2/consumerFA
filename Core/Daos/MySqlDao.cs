using Microsoft.Data.SqlClient;

namespace Core.Daos;

/// <summary>A row of InboundMappingConfig, as stored. Validating it is the caller's job.</summary>
public sealed record InboundMappingConfig(string MappingConfig, string DestinationUrl);

/// <summary>SQL Server access. Reads SqlConnectionString from the environment.</summary>
public class MySqlDao : IMySqlDao
{
    public async Task<InboundMappingConfig> GetInboundMappingConfigAsync(
        string eventName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is not configured."));
        await connection.OpenAsync(cancellationToken);

        // TOP 1 + ORDER BY: EventName has no unique constraint, so without this the row is arbitrary.
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 MappingConfig, DestinationUrl
            FROM InboundMappingConfig
            WHERE EventName = @eventName
            ORDER BY Id DESC
            """,
            connection);
        command.Parameters.Add("@eventName", System.Data.SqlDbType.NVarChar, 260).Value = eventName;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"No InboundMappingConfig row for EventName '{eventName}'.");

        return new(
            Required(reader, 0, "MappingConfig", eventName),
            Required(reader, 1, "DestinationUrl", eventName));
    }

    private static string Required(SqlDataReader reader, int ordinal, string column, string eventName) =>
        reader.IsDBNull(ordinal)
            ? throw new InvalidOperationException(
                $"InboundMappingConfig.{column} is NULL for EventName '{eventName}'.")
            : reader.GetString(ordinal);
}
