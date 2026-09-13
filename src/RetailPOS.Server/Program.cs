using RetailPOS.Contracts.Common;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue<int>("ServerConfiguration:HttpPort", 5100);
    options.ListenAnyIP(port);
});

var app = builder.Build();

app.MapGet("/api/v1/health", () =>
{
    var healthInfo = new
    {
        Status = "Healthy",
        Version = "1.0.0",
        Runtime = ".NET 10",
        TimestampUtc = DateTime.UtcNow
    };
    return Results.Ok(ApiResponse.Ok(healthInfo));
});

app.Run();
