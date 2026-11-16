using Azure.Messaging.ServiceBus;
using Core.Functions;
using Core.Services;
using Microsoft.Azure.Functions.Worker;

namespace CoreUnitTest.Functions;

/// <summary>The binding only: what reaches the service, and how its answer settles the message.</summary>
public class MyFirstQueueFunctionTests
{
    private sealed class FakeService(Func<ProcessResult> answer) : IMappingService
    {
        public (string EventName, string MessageId, int DeliveryCount, string Raw)? Call;

        public Task<ProcessResult> ProcessAsync(
            string eventName, string messageId, int deliveryCount, string raw,
            CancellationToken cancellationToken)
        {
            Call = (eventName, messageId, deliveryCount, raw);
            return Task.FromResult(answer());
        }
    }

    private sealed class FakeActions : ServiceBusMessageActions
    {
        public List<string> Settlements = [];
        public string? Reason;
        public string? Description;

        public override Task CompleteMessageAsync(
            ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            Settlements.Add("Complete");
            return Task.CompletedTask;
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            Dictionary<string, object>? propertiesToModify = null,
            string? deadLetterReason = null,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default)
        {
            Settlements.Add("DeadLetter");
            Reason = deadLetterReason;
            Description = deadLetterErrorDescription;
            return Task.CompletedTask;
        }
    }

    private static readonly ServiceBusReceivedMessage Message = ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: BinaryData.FromString("""{"orderId":"A1"}"""), messageId: "sb-1", deliveryCount: 3);

    private static async Task<FakeActions> Run(FakeService service)
    {
        var actions = new FakeActions();
        await new MyFirstQueueFunction(service).Run(Message, actions, default);
        return actions;
    }

    [Fact]
    public async Task HandsTheMessageToTheServiceUnderItsQueueName()
    {
        var service = new FakeService(() => ProcessResult.Delivered);

        await Run(service);

        Assert.Equal(("myFirstQueue", "sb-1", 3, """{"orderId":"A1"}"""), service.Call);
    }

    [Fact]
    public async Task CompletesADeliveredMessage()
    {
        var actions = await Run(new FakeService(() => ProcessResult.Delivered));

        Assert.Equal(["Complete"], actions.Settlements);
    }

    [Fact]
    public async Task DeadLettersWithTheReasonTheServiceGave()
    {
        var actions = await Run(new FakeService(() => new("MappingFailed", "orderId is missing")));

        Assert.Equal(["DeadLetter"], actions.Settlements);
        Assert.Equal("MappingFailed", actions.Reason);
        Assert.Equal("orderId is missing", actions.Description);
    }

    [Fact]
    public async Task LeavesTheMessageUnsettledWhenTheServiceThrows()
    {
        // Settling nothing is what makes Service Bus redeliver it once the lock lapses.
        var actions = new FakeActions();
        var function = new MyFirstQueueFunction(new FakeService(() => throw new HttpRequestException("503")));

        await Assert.ThrowsAsync<HttpRequestException>(() => function.Run(Message, actions, default));

        Assert.Empty(actions.Settlements);
    }
}
