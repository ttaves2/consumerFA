using Core.Daos;
using Microsoft.Data.SqlClient;

namespace CoreUnitTest.Daos;

/// <summary>Runs where SQL Server LocalDB is installed, and is reported as skipped elsewhere.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!LocalDbFixture.IsAvailable) Skip = "SQL Server LocalDB is not available.";
    }
}

/// <summary>A throwaway database for the test run, dropped afterwards.</summary>
public sealed class LocalDbFixture : IDisposable
{
    private const string Master =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var connection = new SqlConnection(Master);
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    private readonly string database = "consumerFA_" + Guid.NewGuid().ToString("N");

    public string ConnectionString { get; }

    public LocalDbFixture()
    {
        ConnectionString = new SqlConnectionStringBuilder(Master) { InitialCatalog = database }.ConnectionString;
        if (!IsAvailable) return;

        Execute(Master, $"CREATE DATABASE [{database}]");

        // Looser than the README DDL on purpose: no UNIQUE on EventName and nullable columns, so
        // the guards against duplicate rows and NULLs can actually be exercised.
        Execute(ConnectionString,
            """
            CREATE TABLE InboundMappingConfig (
                Id                   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                EventName            nvarchar(260)  NOT NULL,
                MappingConfig        nvarchar(max)  NULL,
                LastUpdatedBy        nvarchar(25)   NULL,
                LastUpdatedTimestamp datetime2(3)   NULL,
                DestinationUrl       nvarchar(2048) NULL
            )
            """);
    }

    public void Insert(string eventName, string? mappingConfig, string? destinationUrl) =>
        Execute(ConnectionString,
            "INSERT INTO InboundMappingConfig (EventName, MappingConfig, DestinationUrl) VALUES (@e, @m, @d)",
            new SqlParameter("@e", eventName),
            new SqlParameter("@m", (object?)mappingConfig ?? DBNull.Value),
            new SqlParameter("@d", (object?)destinationUrl ?? DBNull.Value));

    public void Dispose()
    {
        if (!IsAvailable) return;

        // Pooled connections would otherwise hold the database open and block the drop.
        SqlConnection.ClearAllPools();
        Execute(Master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }

    private static void Execute(string connectionString, string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }
}

public class MySqlDaoTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    // Each test uses its own event name, since they share one database.
    private static string NewEvent() => "event-" + Guid.NewGuid().ToString("N");

    private Task<InboundMappingConfig> Get(string eventName) =>
        new MySqlDao(db.ConnectionString).GetInboundMappingConfigAsync(eventName, default);

    [LocalDbFact]
    public async Task ReturnsTheRowForTheEvent()
    {
        var eventName = NewEvent();
        db.Insert(NewEvent(), "other mapping", "https://other.example");
        db.Insert(eventName, """{"id": orderId}""", "https://dest.example");

        var row = await Get(eventName);

        Assert.Equal(new InboundMappingConfig("""{"id": orderId}""", "https://dest.example"), row);
    }

    [LocalDbFact]
    public async Task PrefersTheNewestRowWhenAnEventHasSeveral()
    {
        var eventName = NewEvent();
        db.Insert(eventName, "old", "https://old.example");
        db.Insert(eventName, "new", "https://new.example");

        Assert.Equal("new", (await Get(eventName)).MappingConfig);
    }

    [LocalDbFact]
    public async Task ThrowsWhenTheEventHasNoRow()
    {
        var eventName = NewEvent();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Get(eventName));

        Assert.Contains(eventName, error.Message);
    }

    [LocalDbFact]
    public async Task ThrowsNamingANullMappingConfig()
    {
        var eventName = NewEvent();
        db.Insert(eventName, null, "https://dest.example");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Get(eventName));

        Assert.Contains("MappingConfig is NULL", error.Message);
    }

    [LocalDbFact]
    public async Task ThrowsNamingANullDestinationUrl()
    {
        var eventName = NewEvent();
        db.Insert(eventName, "mapping", null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Get(eventName));

        Assert.Contains("DestinationUrl is NULL", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ThrowsWhenNoConnectionStringIsConfigured(string? connectionString)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new MySqlDao(connectionString).GetInboundMappingConfigAsync("any", default));

        Assert.Contains("SqlConnectionString", error.Message);
    }
}
