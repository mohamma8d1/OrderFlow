namespace OrderFlow.DataAccess.Messaging;

/// <summary>
/// Announces that a product changed. Called AFTER the database commit, so subscribers
/// never hear about a change that was rolled back.
/// Implementations must never throw: a failed announcement must not fail the request.
/// </summary>
public interface IProductChangePublisher
{
    Task PublishAsync(Guid productId, string change, CancellationToken cancellationToken = default);
}
