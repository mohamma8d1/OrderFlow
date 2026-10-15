using Microsoft.AspNetCore.Mvc.Testing;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OrderFlow.Api.Tests;

public class AuthenticationTests : IClassFixture<OrderFlowApiFactory>
{
    private readonly OrderFlowApiFactory _factory;

    public AuthenticationTests(OrderFlowApiFactory factory)
    {
        _factory = factory;
    }


    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Category");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_InvalidToken_Returns401()
    {
        using var client = _factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "Invalid-token");

        var response = await client.GetAsync("/api/Category");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
