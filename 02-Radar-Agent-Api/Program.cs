using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using radar_agent_api.Agents.Tools;
using radar_agent_api.Data;
using Scalar.AspNetCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<TicketTools>();

var ollamaUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434/";

// 2. REGISTRIAMO IL CLIENT OLLAMA NELLA DI
// Usiamo AddSingleton perché il client HTTP e la connessione a Ollama vanno riutilizzati
builder.Services.AddSingleton<IChatClient>(_ =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(ollamaUrl),
        Timeout = TimeSpan.FromMinutes(5)
    };
    return new OllamaApiClient(httpClient, "qwen3:4b-instruct-2507-q4_K_M");
});

builder.Services.AddScoped<AIAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var ticketTools = sp.GetRequiredService<TicketTools>();

    AIFunction searchTool = AIFunctionFactory.Create(ticketTools.SearchTicketsAsync, name: "SearchTickets");
    AIFunction createTool = AIFunctionFactory.Create(ticketTools.CreateTicketAsync, name: "CreateTickets");
        
    return chatClient.AsAIAgent(
        name: "DatabaseSupportAgent",
        instructions: """
            Sei un assistente IT collegato al database aziendale dei ticket.
            - Quando l'utente chiede di cercare o vedere i ticket, usa il tool SearchTickets passando lo stato ('Open' o 'Closed') oppure vuoto per vederli tutti.
            - Quando l'utente chiede di aprire o creare un nuovo ticket, usa il tool CreateTickets specificando titolo e priorità.
            Rispondi sempre in italiano in modo chiaro e sintetico.
            """,
        tools: [ searchTool, createTool ]
    );
});

builder.Services.AddSingleton<ConcurrentDictionary<string, AgentSession>>();

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

app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.MapPost("/chat", async (
    ChatRequest request,
    AIAgent agent,
    ConcurrentDictionary<string, AgentSession> sessions) =>
{
    if (string.IsNullOrWhiteSpace(request.ConversationId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest("ConversationId e Message sono obbligatori.");
    }

    // Recuperiamo la sessione esistente per quel conversationId, oppure ne creiamo una nuova
    if (!sessions.TryGetValue(request.ConversationId, out var session))
    {
        session = await agent.CreateSessionAsync();
        sessions[request.ConversationId] = session;
    }

    // Eseguiamo l'agente passando sia il messaggio che la sessione specifica di questa chat
    AgentResponse response = await agent.RunAsync(request.Message, session);

    return Results.Ok(new ChatResponse(request.ConversationId, response.Text));
})          
    .WithName("ChatWithAgent")
    .WithSummary("Invia un messaggio all'agente di supporto mantenendo lo stato tramite conversationId");

app.MapPost("/chat/raw", async (
    ChatRequest request,
    AIAgent agent,
    ConcurrentDictionary<string, AgentSession> sessions) =>
{
    if (!sessions.TryGetValue(request.ConversationId, out var session))
    {
        session = await agent.CreateSessionAsync();
        sessions[request.ConversationId] = session;
    }

    AgentResponse response = await agent.RunAsync(request.Message, session);
    return Results.Text(response.Text, "text/plain; charset=utf-8");
});

app.Run();

// DTO (Data Transfer Objects) per il body JSON in ingresso e in uscita
public record ChatRequest(string ConversationId, string Message);
public record ChatResponse(string ConversationId, string Reply);