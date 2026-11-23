using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using radar_agent_api.Agents;
using radar_agent_api.Agents.Tools;
using radar_agent_api.Data;
using radar_agent_api.Store;
using Scalar.AspNetCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<TicketTools>();

var ollamaUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434/";

builder.Services.AddSingleton<PersistentSessionStore>();

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
    AIFunction createTool = AIFunctionFactory.Create(ticketTools.CreateTicketAsync, name: "CreateTicket");
    AIFunction approvalRequiredCreateTicket = new ApprovalRequiredAIFunction(createTool);
        
    return chatClient.AsAIAgent(
        name: "DatabaseSupportAgent",
        instructions: """
            Sei un assistente IT collegato al database aziendale dei ticket.
            - Quando l'utente chiede di cercare o vedere i ticket, usa il tool SearchTickets passando lo stato ('Open' o 'Closed') oppure vuoto per vederli tutti.
            - Quando l'utente chiede di aprire o creare un nuovo ticket, usa il tool CreateTicket specificando titolo e priorità ('Low', 'Medium', 'High', 'Critical').
            Rispondi sempre in italiano in modo chiaro e sintetico.
            """,
        tools: [ searchTool, approvalRequiredCreateTicket]
    );
});

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
    PersistentSessionStore sessionStore,
    AppDbContext dbContext,
    CancellationToken ct) =>
{
    // 1. Ripristina da RAM, oppure da SQL, oppure crea nuova sessione
    var session = await sessionStore.GetOrCreateAsync(agent, request.ConversationId, ct);

    // 2. Esegui l'agente con il contesto ripristinato
    var response = await agent.RunAsync(request.Message, session, cancellationToken: ct);

    // 3. Serializza e persisti subito lo stato aggiornato su SQL + Cache
    await sessionStore.SaveAsync(agent, request.ConversationId, session, ct);

    var result = await AgentHelpers.HandleAgentResponseAsync(request.ConversationId, response, dbContext);
    return Results.Ok(result);
})          
    .WithName("ChatWithAgent")
    .WithSummary("Invia un messaggio all'agente di supporto mantenendo lo stato tramite conversationId");

app.MapPost("/chat/raw", async (
    ChatRequest request,
    AIAgent agent,
    PersistentSessionStore sessionStore,
    CancellationToken ct) =>
{
    var session = await sessionStore.GetOrCreateAsync(agent, request.ConversationId, ct);

    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);

    await sessionStore.SaveAsync(agent, request.ConversationId, session, ct);

    return Results.Text(response.Text, "text/plain; charset=utf-8");
});

app.MapPost("/chat/approve", async (
    ApprovalDecisionRequest decision,
    AIAgent agent,
    PersistentSessionStore sessionStore,
    AppDbContext dbContext,
    CancellationToken ct) =>
{
    // 1. Recupero la richiesta di approvazione salvata su SQL Server
    var pending = await dbContext.PendingApprovals
        .FirstOrDefaultAsync(p => p.RequestId == decision.RequestId
            && p.ConversationId == decision.ConversationId, ct);

    if (pending is null)
        return Results.NotFound("Richiesta di approvazione non trovata o già gestita.");

    // 2. Deserializzo il ToolApprovalRequestContent originale
    var requestContent = JsonSerializer.Deserialize<AIContent>(
        pending.SerializedRequestJson,
        AIJsonUtilities.DefaultOptions) as ToolApprovalRequestContent;

    if (requestContent is null)
        return Results.Problem("Impossibile deserializzare la richiesta di approvazione.");

    var session = await sessionStore.GetOrCreateAsync(agent, decision.ConversationId, ct);

    // 3. Creo la risposta (true/false) e incapsulalo in un ChatMessage con ruolo User
    var approvalResponse = requestContent.CreateResponse(decision.Approved);
    var approvalMessage = new ChatMessage(ChatRole.User, [approvalResponse]);

    AgentResponse response = await agent.RunAsync(approvalMessage, session, cancellationToken: ct);

    // 4. Rimuovi la richiesta appena gestita dal DB e salva lo stato aggiornato della sessione
    dbContext.PendingApprovals.Remove(pending);
    await dbContext.SaveChangesAsync(ct);
    await sessionStore.SaveAsync(agent, decision.ConversationId, session, ct);

    var result = await AgentHelpers.HandleAgentResponseAsync(decision.ConversationId, response, dbContext);
    return Results.Ok(result);
})
    .WithName("ApproveRequest")
    .WithSummary("Approva o rifiuta una richiesta di approvazione");

app.Run();