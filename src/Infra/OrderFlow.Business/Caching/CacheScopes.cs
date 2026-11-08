namespace OrderFlow.Business.Caching;

/// <summary>Cache scopes (groups of entries invalidated together) and their lifetimes.</summary>
internal static class CacheScopes
{
    /// <summary>Product/Get. Bumped by any product change AND any stock change (orders), because the list shows quantity.</summary>
    public const string ProductList = "products:list";

    /// <summary>Category/GetAll and Category/GetById. Categories rarely change, so one scope for all of them.</summary>
    public const string Categories = "categories";

    /// <summary>Order/GetMyOrders and Order/GetById of one user. The user id in the scope keeps users apart.</summary>
    public static string UserOrders(Guid userId) => $"orders:user:{userId:D}";

    public static readonly TimeSpan ProductListTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CategoriesTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan UserOrdersTtl = TimeSpan.FromMinutes(5);
}
