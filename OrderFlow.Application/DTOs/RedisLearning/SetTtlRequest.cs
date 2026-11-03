using System;

namespace OrderFlow.Application.DTOs.RedisLearning;

public record SetTtlRequest(string Value, int TtlSeconds);

