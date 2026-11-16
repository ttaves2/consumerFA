using Azure.Messaging.ServiceBus;
using Core.Services;
using Microsoft.Azure.Functions.Worker;

namespace Core.Functions;

/// <summary>Service Bus binding for myFirstQueue. The pipeline itself lives in MappingService.</summary>
public class MyFirstQueueFunction(IMappingService service)
{
    private const string QueueName = "myFirstQueue";

    // host.json sets autoCompleteMessages: false, so every path below must settle the message.
    // When the service throws, the message is left unsettled and comes back when the lock lapses.
    [Function(nameof(MyFirstQueueFunction))]
    public async Task Run(
        [ServiceBusTrigger(QueueName, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        var result = await service.ProcessAsync(
            QueueName, message.MessageId, message.DeliveryCount, message.Body.ToString(), cancellationToken);

        if (result.IsDelivered)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        await messageActions.DeadLetterMessageAsync(
            message,
            deadLetterReason: result.DeadLetterReason,
            deadLetterErrorDescription: result.DeadLetterDescription,
            cancellationToken: cancellationToken);
    }
}
