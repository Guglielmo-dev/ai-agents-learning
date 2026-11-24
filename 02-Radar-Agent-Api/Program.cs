using Microsoft.EntityFrameworkCore;
using radar_agent_api.Data;
using radar_agent_api.Endpoints.ChatEndpoints;
using radar_agent_api.Extensions;
using radar_agent_api.Telemetry;
using Scalar.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("radar-agent-api"))
    .WithTracing(tr => tr
        .AddSource(AgentTelemetry.SourceName)   // Raccoglie le tracce emesse dall'agente
        .AddAspNetCoreInstrumentation()         // Cattura gli endpoint /chat, /chat/stream, /chat/approve
        .AddHttpClientInstrumentation()         // Cattura le chiamate HTTP verso Ollama
        .AddOtlpExporter());                    // Legge automaticamente OTEL_EXPORTER_OTLP_ENDPOINT

builder.Services.AddCors(options =>
{
    options.AddPolicy("RadarUiCors", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddRadarAgent(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors("RadarUiCors");

app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.MapChatEndpoints();

app.Run();
