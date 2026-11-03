using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Application.DTOs.RedisLearning;
using StackExchange.Redis;

namespace OrderFlow.Api.Controllers;

[Route("[controller]")]
[ApiController]
public class RedisLearningController(IConnectionMultiplexer redis) : ControllerBase
{

    private const string StreamKey = "orderflow:events:stream";
    private const string GroupName = "orderflow-workers";
    private const string DefaultConsumerName = "worker-1";

    [HttpGet]
    [Route("[action]")]
    public async Task<IActionResult> Ping()
    {
        var database = redis.GetDatabase();

        var latency = await database.PingAsync();

        return Ok(new
                {
                    Status = "Connected",
                    LatencyMillisecond = latency.TotalMilliseconds
                }
            );
    }

    [HttpPost]
    [Route("[action]")]
    public async Task<IActionResult> SetWithTtl([FromBody] SetTtlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Value) || request.TtlSeconds <= 0 || request.TtlSeconds > 3600)
            return BadRequest();
    

        var database = redis.GetDatabase();

        var id = Guid.NewGuid();

        var key = $"orderflow:learning:ttl:{id}";

        await database.StringSetAsync(
            key,
            request.Value,
            TimeSpan.FromSeconds(request.TtlSeconds));

        return CreatedAtAction(
            nameof(GetWithTtl),
            new { id },
            new { id, request.TtlSeconds });
    }

    [HttpGet]
    [Route("[action]/{id:guid}")]
    public async Task<IActionResult> GetWithTtl(Guid id)
    {
        var database = redis.GetDatabase();

        var key = $"orderflow:learning:ttl:{id}";

        var value = await database.StringGetAsync(key);

        if (value.IsNull)
            return NotFound();

        var ttl = await database.KeyTimeToLiveAsync(key);

        return Ok(new
        {
            Value = value.ToString(),
            RemainingSeconds = ttl?.TotalSeconds
        });
    }

    [HttpPost]
    [Route("[action]")]
    public async Task<IActionResult> Publish([FromBody] PublishRedisMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest();

        var subscriber = redis.GetSubscriber();

        var channel = RedisChannel.Literal("orderflow:events");

        var receivers = await subscriber.PublishAsync(channel, request.Message);

        return Ok(new
        {
            Message = "Message published successfully.",
            SubscribersNotified = receivers
        });
    }

    [HttpPost]
    [Route("[action]")]
    public async Task<IActionResult> AddStreamMessage([FromBody] AddStreamMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventType) || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest();

        var database = redis.GetDatabase();

        var messageId = await database.StreamAddAsync(
            StreamKey,
            new[]
            {
            new NameValueEntry("eventType", request.EventType),
            new NameValueEntry("message", request.Message),
            new NameValueEntry("createdAtUtc", DateTimeOffset.UtcNow.ToString("O"))
            });

        return Ok(new
        {
            Id = messageId.ToString(),
            Status = "Stored"
        });
    }

    [HttpGet]
    [Route("[action]")]
    public async Task<IActionResult> GetStreamMessages([FromQuery] int count = 10)
    {
        if (count < 1 || count > 100)
            return BadRequest("Count must be between 1 and 100.");

        var database = redis.GetDatabase();

        var entries = await database.StreamRangeAsync(
            StreamKey,
            count: count,
            messageOrder: Order.Descending);

        var result = entries.Select(entry =>
        {
            var fields = entry.Values.ToDictionary(
                field => field.Name.ToString(),
                field => field.Value.ToString());

            return new
            {
                Id = entry.Id.ToString(),
                EventType = fields["eventType"],
                Message = fields["message"],
                CreatedAtUtc = fields["createdAtUtc"]
            };
        });

        return Ok(result);
    }
    [HttpPost]
    [Route("[action]")]
    public async Task<IActionResult> ConsumeMessages([FromQuery] int count = 5, [FromQuery] string consumerName = DefaultConsumerName)
    {
        if (count < 1 || count > 100)
            return BadRequest("Count must be between 1 and 100.");

        if (string.IsNullOrWhiteSpace(consumerName))
            return BadRequest("Consumer name is required.");

        var database = redis.GetDatabase();

        var entries = await database.StreamReadGroupAsync(
            StreamKey,
            GroupName,
            consumerName,
            ">",
            count,
            CommandFlags.None);

        var result = entries.Select(entry =>
        {
            var fields = entry.Values.ToDictionary(
                x => x.Name.ToString(),
                x => x.Value.ToString());

            return new
            {
                Id = entry.Id.ToString(),
                Consumer = consumerName,
                EventType = fields["eventType"],
                Message = fields["message"]
            };
        });

        return Ok(result);
    }
    [HttpGet]
    [Route("[action]")]
    public async Task<IActionResult> GetPendingMessages()
    {
        var database = redis.GetDatabase();

        var pending = await database.StreamPendingAsync(StreamKey, GroupName);

        return Ok(new
        {
            PendingCount = pending.PendingMessageCount,
            LowestPendingId = pending.LowestPendingMessageId.ToString(),
            HighestPendingId = pending.HighestPendingMessageId.ToString(),
            Consumers = pending.Consumers.Select(c => new
            {
                ConsumerName = c.Name,
                PendingCount = c.PendingMessageCount
            })
        });
    }

    [HttpPost]
    [Route("[action]/{messageId}/ack")]
    public async Task<IActionResult> AcknowledgeMessage(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return BadRequest();

        var database = redis.GetDatabase();

        var acknowledged = await database.StreamAcknowledgeAsync(StreamKey, GroupName, messageId);

        if (acknowledged == 0)
            return NotFound("Message was not found in pending list.");

        return Ok(new
        {
            MessageId = messageId,
            Acknowledged = true
        });
    }
}
