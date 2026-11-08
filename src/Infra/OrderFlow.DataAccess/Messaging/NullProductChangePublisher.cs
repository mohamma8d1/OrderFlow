namespace OrderFlow.DataAccess.Messaging;

/// <summary>Publishes nothing. Used when Redis is not configured (for example in tests).</summary>
public sealed class NullProductChangePublisher : IProductChangePublisher
{
    public Task PublishAsync(Guid productId, string change, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
