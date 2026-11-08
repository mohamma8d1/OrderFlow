namespace OrderFlow.Common.Events;

/// <summary>
/// Sent on the Redis Pub/Sub channel "orderflow:products:changed" after a product or its stock changed.
/// Pub/Sub is fire-and-forget: a subscriber that is offline when this is sent never sees it.
/// </summary>
public sealed record ProductChangedEvent(Guid ProductId, string Change, DateTime OccurredAtUtc);

public static class ProductChangeKinds
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
    public const string StockDecreased = "stock-decreased";
    public const string StockRestored = "stock-restored";
}
