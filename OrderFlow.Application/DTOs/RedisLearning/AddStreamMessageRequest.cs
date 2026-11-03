using System;

namespace OrderFlow.Application.DTOs.RedisLearning;

public sealed record AddStreamMessageRequest(string EventType, string Message);
