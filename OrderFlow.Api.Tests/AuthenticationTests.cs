 using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
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

    [Fact]
    public async Task ProtectedEndpoint_ValidToken_Returns200AndUserId()
    {
        using var client = _factory.CreateClient();

        var userId = Guid.NewGuid();

        var token = CreateToken(userId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/Category");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Customer_CannotCreateProduct_Returns403()
    {
        using var client = _factory.CreateClient();

        var token = CreateToken(Guid.NewGuid(), "Customer");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            name = "Test Product",
            description = "Test",
            price = 100m,
            quantity = 5,
            categoryId = Guid.NewGuid()
        };

        var response = await client.PostAsJsonAsync("/api/Product",request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private string CreateToken(Guid userId, string role = "Customer")
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

        var credentials = new SigningCredentials(key,SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "test@example.com"),
            new Claim(ClaimTypes.Role, role)
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
