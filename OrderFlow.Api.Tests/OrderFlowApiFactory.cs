using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Api.Tests;

public class OrderFlowApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            var testSettings = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "OrderFlow-TEST-ONLY-Signing-Key-Do-Not-Use-In-Production-2026!",
                ["Jwt:Issuer"] = "OrderFlow.Tests",
                ["Jwt:Audience"] = "OrderFlow.Tests.Client",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=MOHAMMAD-LOQ;Initial Catalog=OrderFlow_Test;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;"
            };

            configuration.AddInMemoryCollection(testSettings);
        });
    }
}