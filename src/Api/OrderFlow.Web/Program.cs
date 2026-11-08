using OrderFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .InjectControllersAndApi()
    .InjectSwagger()
    .InjectJwtAuthentication(builder.Configuration)
    .InjectDataAccess(builder.Configuration)
    .InjectBusinesses();

// Tests run without Redis: they get the "Null" cache/limiter from InjectDataAccess,
// or swap in a real Redis (Testcontainers) themselves.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services
        .InjectRedis(builder.Configuration)
        .InjectMessaging();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Lets the tests start the host with WebApplicationFactory<Program>
public partial class Program;
