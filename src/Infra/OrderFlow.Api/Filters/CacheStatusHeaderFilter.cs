using Microsoft.AspNetCore.Mvc.Filters;
using OrderFlow.DataAccess.Caching;

namespace OrderFlow.Api.Filters;

/// <summary>
/// Adds "X-Cache: HIT" or "X-Cache: MISS" to every response whose data went through a cache.
/// Responses that never touched a cache (POST, PUT, DELETE, login...) get no header.
/// </summary>
public sealed class CacheStatusHeaderFilter(CacheOutcome cacheOutcome) : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (cacheOutcome.IsHit is { } isHit)
            context.HttpContext.Response.Headers["X-Cache"] = isHit ? "HIT" : "MISS";
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
