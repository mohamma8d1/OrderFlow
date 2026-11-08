using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using OrderFlow.DataAccess.Caching;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace OrderFlow.Api.Filters;

/// <summary>
/// Limits an action to <paramref name="limit"/> calls per <paramref name="windowSeconds"/> for each user
/// (or each IP address when nobody is signed in). The counting happens in Redis, so the limit holds
/// across every instance of the API.
///
/// Usage: <c>[RateLimit("order-create", limit: 5, windowSeconds: 60)]</c>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RateLimitAttribute(string policy, int limit, int windowSeconds) : Attribute, IFilterFactory
{
    public string Policy { get; } = policy;
    public int Limit { get; } = limit;
    public TimeSpan Window { get; } = TimeSpan.FromSeconds(windowSeconds);

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new RateLimitFilter(serviceProvider.GetRequiredService<IRateLimiter>(), this);

    private sealed class RateLimitFilter(IRateLimiter rateLimiter, RateLimitAttribute settings) : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;

            var decision = await rateLimiter.HitAsync(
                settings.Policy,
                PartitionFor(http),
                settings.Limit,
                settings.Window,
                http.RequestAborted);

            // Standard-ish headers so a client can see where it stands.
            http.Response.Headers["X-RateLimit-Limit"] = settings.Limit.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

            if (decision.IsAllowed)
            {
                await next();
                return;
            }

            var retryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds);
            http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            context.Result = new ObjectResult($"Too many requests. Try again in {retryAfterSeconds} seconds.")
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
        }

        private static string PartitionFor(HttpContext http)
        {
            var userId = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub);

            return !string.IsNullOrEmpty(userId)
                ? $"user:{userId}"
                : $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        }
    }
}
