using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using radar_agent_api.Agents.Tools;
using radar_agent_api.Contracts.Chat;
using radar_agent_api.Data;
using radar_agent_api.Endpoints.ChatEndpoints;
using radar_agent_api.Extensions;
using radar_agent_api.Services;
using radar_agent_api.Store;
using ApiChatResponse = radar_agent_api.Contracts.Chat.ChatResponse;
using ModelChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace radar_agent_api.Tests;

public class ChatRefactoringTests
{
    [Fact]
    public async Task Chat_ReturnsExistingJsonContractAndPreservesHistoryAcrossRequests()
    {
        var model = new ScriptedChatClient(Reply("Prima risposta"), Reply("Seconda risposta"));
        await using var host = await ChatTestHost.CreateAsync(model);

        using var first = await host.Client.PostAsJsonAsync("/chat", new ChatRequest("history", "Primo messaggio"));
        var firstBody = await first.Content.ReadFromJsonAsync<ApiChatResponse>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("history", firstBody!.ConversationId);
        Assert.Equal("Prima risposta", firstBody.Reply);
        Assert.Null(firstBody.PendingApprovals);
        Assert.Equal(string.Join("\r\n",
        [
            "Sei un assistente IT collegato al database aziendale dei ticket.",
            "- Quando l'utente chiede di cercare o vedere i ticket, usa il tool SearchTickets passando lo stato ('Open' o 'Closed') oppure vuoto per vederli tutti.",
            "- Quando l'utente chiede di aprire o creare un nuovo ticket, usa il tool CreateTicket specificando titolo e priorità ('Low', 'Medium', 'High', 'Critical').",
            "Rispondi sempre in italiano in modo chiaro e sintetico."
        ]), model.Instructions[0]);

        using var second = await host.Client.PostAsJsonAsync("/chat", new ChatRequest("history", "Secondo messaggio"));
        second.EnsureSuccessStatusCode();
        Assert.Contains(model.Requests[1], message => message.Text == "Primo messaggio");
        Assert.Contains(model.Requests[1], message => message.Text == "Prima risposta");
        Assert.Equal(1, await host.QueryAsync(db => db.AgentConversations.CountAsync()));

        var json = await first.Content.ReadAsStringAsync();
        Assert.Contains("\"conversationId\"", json);
        Assert.Contains("\"reply\"", json);
        Assert.Contains("\"pendingApprovals\"", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approval_ExecutesCreationOnlyWhenApproved(bool approved)
    {
        var model = new ScriptedChatClient(CreateTicketCall(), Reply("Decisione elaborata"));
        await using var host = await ChatTestHost.CreateAsync(model);

        using var initial = await host.Client.PostAsJsonAsync("/chat", new ChatRequest("approval", "Apri un ticket"));
        initial.EnsureSuccessStatusCode();
        var body = await initial.Content.ReadFromJsonAsync<ApiChatResponse>();
        var pending = Assert.Single(body!.PendingApprovals!);
        Assert.Equal("Azione in attesa di approvazione umana", body.Reply);
        Assert.Equal("CreateTicket", pending.FunctionName);
        Assert.NotNull(pending.Arguments);
        Assert.Equal(0, await host.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Equal(1, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));

        using var result = await host.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("approval", pending.RequestId, approved));

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var response = await result.Content.ReadFromJsonAsync<ApiChatResponse>();
        Assert.Equal("approval", response!.ConversationId);
        Assert.Equal("Decisione elaborata", response.Reply);
        Assert.Equal(approved ? 1 : 0, await host.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Equal(0, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));

        using var repeated = await host.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("approval", pending.RequestId, approved));
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task Approval_WhenMissing_ReturnsExistingNotFoundResponse()
    {
        var model = new ScriptedChatClient();
        await using var host = await ChatTestHost.CreateAsync(model);

        using var response = await host.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("missing", "unknown", true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Richiesta di approvazione non trovata o già gestita.",
            await response.Content.ReadFromJsonAsync<string>());
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Approval_FromAnotherConversation_DoesNotExecuteOrConsumeRequest()
    {
        var model = new ScriptedChatClient(CreateTicketCall());
        await using var host = await ChatTestHost.CreateAsync(model);
        using var initial = await host.Client.PostAsJsonAsync("/chat", new ChatRequest("owner", "Apri un ticket"));
        initial.EnsureSuccessStatusCode();
        var body = await initial.Content.ReadFromJsonAsync<ApiChatResponse>();
        var pending = Assert.Single(body!.PendingApprovals!);

        using var response = await host.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("other", pending.RequestId, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));
        Assert.Equal(0, await host.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Approval_WithUnexpectedStoredContent_ReturnsExistingProblem()
    {
        var model = new ScriptedChatClient();
        await using var host = await ChatTestHost.CreateAsync(model);
        await host.QueryAsync(async db =>
        {
            db.PendingApprovals.Add(new PendingApproval
            {
                RequestId = "invalid",
                ConversationId = "invalid-conversation",
                FunctionName = "CreateTicket",
                SerializedRequestJson = JsonSerializer.Serialize<AIContent>(
                    new TextContent("Not an approval"), AIJsonUtilities.DefaultOptions)
            });
            return await db.SaveChangesAsync();
        });

        using var response = await host.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("invalid-conversation", "invalid", true));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Impossibile deserializzare la richiesta di approvazione.", problem!.Detail);
        Assert.Equal(1, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Raw_ReturnsPlainTextAndPersistsSession()
    {
        var model = new ScriptedChatClient(Reply("Risposta semplice"));
        await using var host = await ChatTestHost.CreateAsync(model);

        using var response = await host.Client.PostAsJsonAsync("/chat/raw", new ChatRequest("raw", "Ciao"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("Risposta semplice", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await host.QueryAsync(db => db.AgentConversations.CountAsync()));
    }

    [Fact]
    public async Task Raw_WithApproval_PreservesExistingBehaviorWithoutSavingPendingApprovals()
    {
        var model = new ScriptedChatClient(CreateTicketCall());
        await using var host = await ChatTestHost.CreateAsync(model);

        using var response = await host.Client.PostAsJsonAsync("/chat/raw", new ChatRequest("raw-approval", "Apri un ticket"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(0, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));
        Assert.Equal(0, await host.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Equal(1, await host.QueryAsync(db => db.AgentConversations.CountAsync()));
    }

    [Fact]
    public async Task Approval_AfterHostRestart_UsesPersistedSessionAndRequest()
    {
        var databaseName = Guid.NewGuid().ToString();
        var databaseRoot = new InMemoryDatabaseRoot();
        string requestId;
        await using (var firstHost = await ChatTestHost.CreateAsync(
            new ScriptedChatClient(CreateTicketCall()), databaseName, databaseRoot))
        {
            using var initial = await firstHost.Client.PostAsJsonAsync("/chat",
                new ChatRequest("restart", "Apri un ticket"));
            initial.EnsureSuccessStatusCode();
            var body = await initial.Content.ReadFromJsonAsync<ApiChatResponse>();
            requestId = Assert.Single(body!.PendingApprovals!).RequestId;
        }

        var model = new ScriptedChatClient(Reply("Ticket creato dopo riavvio"));
        await using var secondHost = await ChatTestHost.CreateAsync(model, databaseName, databaseRoot);
        using var response = await secondHost.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("restart", requestId, true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiChatResponse>();
        Assert.Equal("Ticket creato dopo riavvio", result!.Reply);
        Assert.Equal(1, await secondHost.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Equal(0, await secondHost.QueryAsync(db => db.PendingApprovals.CountAsync()));
        Assert.Contains(model.Requests.Single(), message => message.Text == "Apri un ticket");
    }

    [Fact]
    public async Task Endpoints_PreserveRoutesNamesAndSummaries()
    {
        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient());
        var routes = host.App.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        Assert.Equal(3, routes.Count);
        Assert.Equal("ChatWithAgent", routes["/chat"].Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("Invia un messaggio all'agente di supporto mantenendo lo stato tramite conversationId",
            routes["/chat"].Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary);
        Assert.Equal("ApproveRequest", routes["/chat/approve"].Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("Approva o rifiuta una richiesta di approvazione",
            routes["/chat/approve"].Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary);
        Assert.All(routes.Values, endpoint =>
            Assert.Equal("POST", Assert.Single(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)));
        Assert.Contains("/chat/raw", routes.Keys);
    }

    [Fact]
    public async Task OpenApi_PreservesMetadataOfOriginalAsyncLambdaMapping()
    {
        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient(), mapOriginalLambda: true);
        var descriptions = host.App.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items).ToList();
        var original = Assert.Single(descriptions, description => description.RelativePath == "original-lambda");
        var current = Assert.Single(descriptions, description => description.RelativePath == "chat");

        Assert.Equal(EffectiveTags(original), EffectiveTags(current));
        Assert.Equal(original.SupportedResponseTypes.Select(response => (response.StatusCode, response.Type)),
            current.SupportedResponseTypes.Select(response => (response.StatusCode, response.Type)));

        static IReadOnlyList<string> EffectiveTags(ApiDescription description)
            => description.ActionDescriptor.EndpointMetadata.OfType<ITagsMetadata>().LastOrDefault()?.Tags
                ?? [description.ActionDescriptor.RouteValues["controller"]!];
    }

    [Fact]
    public async Task Registration_PreservesScopedAndSingletonLifetimes()
    {
        var registrations = new ServiceCollection();
        registrations.AddRadarAgent(new ConfigurationBuilder().Build());
        Assert.Equal(ServiceLifetime.Singleton,
            Assert.Single(registrations, descriptor => descriptor.ServiceType == typeof(IChatClient)).Lifetime);

        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient());
        using var firstScope = host.App.Services.CreateScope();
        using var secondScope = host.App.Services.CreateScope();
        var first = firstScope.ServiceProvider;
        var second = secondScope.ServiceProvider;

        Assert.Same(first.GetRequiredService<AIAgent>(), first.GetRequiredService<AIAgent>());
        Assert.NotSame(first.GetRequiredService<AIAgent>(), second.GetRequiredService<AIAgent>());
        Assert.NotSame(first.GetRequiredService<TicketTools>(), second.GetRequiredService<TicketTools>());
        Assert.NotSame(first.GetRequiredService<ChatService>(), second.GetRequiredService<ChatService>());
        Assert.Same(first.GetRequiredService<PersistentSessionStore>(), second.GetRequiredService<PersistentSessionStore>());
        Assert.Same(first.GetRequiredService<IChatClient>(), second.GetRequiredService<IChatClient>());
    }

    private static ChatMessage Reply(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage CreateTicketCall() => new(ChatRole.Assistant,
    [
        new FunctionCallContent("create-ticket-1", "CreateTicket", new Dictionary<string, object?>
        {
            ["title"] = "Stampante bloccata",
            ["priority"] = "High"
        })
    ]);

    private sealed class ScriptedChatClient(params ChatMessage[] responses) : IChatClient
    {
        private readonly Queue<ChatMessage> _responses = new(responses);
        public List<List<ChatMessage>> Requests { get; } = [];
        public List<string?> Instructions { get; } = [];

        public Task<ModelChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToList());
            Instructions.Add(options?.Instructions);
            return Task.FromResult(new ModelChatResponse(_responses.Dequeue()));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class ChatTestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;

        public static async Task<ChatTestHost> CreateAsync(IChatClient model,
            string? databaseName = null, InMemoryDatabaseRoot? databaseRoot = null,
            bool mapOriginalLambda = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var name = databaseName ?? Guid.NewGuid().ToString();
            var root = databaseRoot ?? new InMemoryDatabaseRoot();
            builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name, root));
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddRadarAgent(builder.Configuration);
            builder.Services.RemoveAll<IChatClient>();
            builder.Services.AddSingleton(model);

            var app = builder.Build();
            try
            {
                app.MapChatEndpoints();
                if (mapOriginalLambda)
                {
                    app.MapPost("/original-lambda", async (ChatRequest request) =>
                    {
                        await Task.CompletedTask;
                        return Results.Ok(new ApiChatResponse(request.ConversationId, "Original response type"));
                    });
                }
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new ChatTestHost(app, new HttpClient { BaseAddress = new Uri(address) });
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
        {
            using var scope = App.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
