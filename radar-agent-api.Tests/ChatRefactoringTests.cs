using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
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
    public async Task Streaming_FlushesChunksBeforeCompletion_ThenSavesSessionAndSendsDone()
    {
        var continueStreaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedChatClient
        {
            StreamingUpdates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, "Ciao "),
                new ChatResponseUpdate(ChatRole.Assistant, "mondo\nSeconda riga")
            ],
            ContinueAfterFirstUpdate = continueStreaming
        };
        await using var host = await ChatTestHost.CreateAsync(model);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string message = "Ciao & grazie?";
        const string conversationId = "stream & query";
        var url = "/chat/stream?conversationId=" + Uri.EscapeDataString(conversationId)
            + "&message=" + Uri.EscapeDataString(message);

        try
        {
            using var response = await host.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var events = SseParser.Create(stream).EnumerateAsync(timeout.Token).GetAsyncEnumerator();

            Assert.True(await events.MoveNextAsync());
            Assert.Equal("chunk", events.Current.EventType);
            Assert.Equal("Ciao ", events.Current.Data);
            Assert.False(continueStreaming.Task.IsCompleted);
            Assert.Equal(0, await host.QueryAsync(db => db.AgentConversations.CountAsync()));

            continueStreaming.TrySetResult(true);
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("chunk", events.Current.EventType);
            Assert.Equal("mondo\nSeconda riga", events.Current.Data);
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("done", events.Current.EventType);
            Assert.Equal("done", events.Current.Data);
            Assert.False(await events.MoveNextAsync());

            Assert.Equal(conversationId, await host.QueryAsync(db => db.AgentConversations.Select(c => c.ConversationId).SingleAsync()));
            Assert.Equal(0, await host.QueryAsync(db => db.PendingApprovals.CountAsync()));
            Assert.Contains(Assert.Single(model.Requests), item => item.Role == ChatRole.User && item.Text == message);
            Assert.Equal(1, model.StreamingCalls);
        }
        finally
        {
            continueStreaming.TrySetResult(true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streaming_PersistsApprovalBeforeSendingIt_AndCanResumeAfterRestart(bool approved)
    {
        var databaseName = Guid.NewGuid().ToString();
        var databaseRoot = new InMemoryDatabaseRoot();
        string requestId;
        await using (var firstHost = await ChatTestHost.CreateAsync(
            new ScriptedChatClient(CreateTicketCall()), databaseName, databaseRoot))
        {
            using var response = await firstHost.Client.GetAsync("/chat/stream?conversationId=stream-approval&message=Apri%20un%20ticket",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var events = new List<SseItem<string>>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            {
                events.Add(item);
                if (item.EventType == "approval")
                {
                    Assert.Equal(1, await firstHost.QueryAsync(db => db.PendingApprovals.CountAsync()));
                    Assert.Equal(1, await firstHost.QueryAsync(db => db.AgentConversations.CountAsync()));
                    Assert.Equal(0, await firstHost.QueryAsync(db => db.Tickets.CountAsync()));
                }
            }

            var approval = Assert.Single(events, item => item.EventType == "approval");
            using var json = JsonDocument.Parse(approval.Data);
            var entry = Assert.Single(json.RootElement.EnumerateArray());
            requestId = entry.GetProperty("requestId").GetString()!;
            Assert.Equal("CreateTicket", entry.GetProperty("functionName").GetString());
            Assert.Equal("Stampante bloccata", entry.GetProperty("arguments").GetProperty("title").GetString());
            Assert.Equal("done", events[^1].EventType);
            Assert.Equal("done", events[^1].Data);
        }

        await using var secondHost = await ChatTestHost.CreateAsync(
            new ScriptedChatClient(Reply("Decisione elaborata")), databaseName, databaseRoot);
        using var result = await secondHost.Client.PostAsJsonAsync("/chat/approve",
            new ApprovalDecisionRequest("stream-approval", requestId, approved));
        result.EnsureSuccessStatusCode();
        var body = await result.Content.ReadFromJsonAsync<ApiChatResponse>();
        Assert.Equal("Decisione elaborata", body!.Reply);
        Assert.Equal(approved ? 1 : 0, await secondHost.QueryAsync(db => db.Tickets.CountAsync()));
        Assert.Equal(0, await secondHost.QueryAsync(db => db.PendingApprovals.CountAsync()));
    }

    [Fact]
    public async Task Streaming_WithNoText_StillSavesSessionAndSendsOnlyDone()
    {
        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient(Reply(string.Empty)));
        using var response = await host.Client.GetAsync("/chat/stream?conversationId=empty-stream&message=Ciao");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var events = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            events.Add(item);

        Assert.Equal("done", Assert.Single(events).EventType);
        Assert.Equal(1, await host.QueryAsync(db => db.AgentConversations.CountAsync()));
    }

    [Fact]
    public async Task Streaming_RespectsEnumeratorCancellation_WithoutPersistingAnIncompleteRun()
    {
        var model = new ScriptedChatClient
        {
            StreamingUpdates = [new ChatResponseUpdate(ChatRole.Assistant, "Primo frammento")],
            ContinueAfterFirstUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var host = await ChatTestHost.CreateAsync(model);
        using var scope = host.App.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var service = scope.ServiceProvider.GetRequiredService<ChatService>();
        await using var events = service.SendMessageStreamingAsync(new ChatRequest("cancel-stream", "Ciao"))
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("chunk", events.Current.EventType);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await events.MoveNextAsync(); });
        Assert.Equal(0, await host.QueryAsync(db => db.AgentConversations.CountAsync()));
    }

    [Theory]
    [InlineData("/chat/stream?conversationId=missing-message")]
    [InlineData("/chat/stream?message=missing-conversation")]
    public async Task Streaming_WithMissingQueryParameters_ReturnsBadRequest(string url)
    {
        var model = new ScriptedChatClient();
        await using var host = await ChatTestHost.CreateAsync(model);
        using var response = await host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Endpoints_PreserveRoutesNamesAndSummaries()
    {
        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient());
        var routes = host.App.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        Assert.Equal(4, routes.Count);
        Assert.Equal("ChatWithAgent", routes["/chat"].Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("Invia un messaggio all'agente di supporto mantenendo lo stato tramite conversationId",
            routes["/chat"].Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary);
        Assert.Equal("ApproveRequest", routes["/chat/approve"].Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("Approva o rifiuta una richiesta di approvazione",
            routes["/chat/approve"].Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary);
        Assert.All(routes.Where(route => route.Key != "/chat/stream").Select(route => route.Value), endpoint =>
            Assert.Equal("POST", Assert.Single(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)));
        Assert.Contains("/chat/raw", routes.Keys);
        Assert.Equal("GET", Assert.Single(routes["/chat/stream"].Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods));
        Assert.Equal("ChatStreamingWithAgent", routes["/chat/stream"].Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Contains(routes["/chat/stream"].Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            metadata => metadata.ContentTypes.Contains("text/event-stream"));
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

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Testing", false)]
    public async Task Telemetry_EnablesMessageContentOnlyInDevelopment(string environmentName, bool expected)
    {
        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient(), environmentName: environmentName);
        using var scope = host.App.Services.CreateScope();
        var agent = Assert.IsType<OpenTelemetryAgent>(scope.ServiceProvider.GetRequiredService<AIAgent>());
        Assert.Equal(expected, agent.EnableSensitiveData);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Telemetry_EmitsCorrelatedAgentActivitiesForNormalAndStreamingRuns(bool streaming)
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RadarAgent",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);

        await using var host = await ChatTestHost.CreateAsync(new ScriptedChatClient(Reply("Risposta con telemetria")));
        using var scope = host.App.Services.CreateScope();
        using var parent = new Activity("telemetry-test-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var service = scope.ServiceProvider.GetRequiredService<ChatService>();
        var request = new ChatRequest("telemetry-test", "Ciao");

        if (streaming)
        {
            var events = new List<SseItem<string>>();
            await foreach (var item in service.SendMessageStreamingAsync(request))
                events.Add(item);
            Assert.Equal("done", events[^1].EventType);
        }
        else
        {
            var response = await service.SendMessageAsync(request);
            Assert.Equal("Risposta con telemetria", response.Reply);
        }

        Assert.Contains(activities, activity =>
            activity.Source.Name == "RadarAgent"
            && activity.GetTagItem("gen_ai.agent.name")?.ToString() == "DatabaseSupportAgent");
        Assert.Contains(activities, activity => activity.TraceId == parent.TraceId && activity.ParentSpanId == parent.SpanId);
        Assert.Equal(1, await host.QueryAsync(db => db.AgentConversations.CountAsync()));
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
        public IReadOnlyList<ChatResponseUpdate>? StreamingUpdates { get; init; }
        public TaskCompletionSource<bool>? ContinueAfterFirstUpdate { get; init; }
        public int StreamingCalls { get; private set; }

        public Task<ModelChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToList());
            Instructions.Add(options?.Instructions);
            return Task.FromResult(new ModelChatResponse(_responses.Dequeue()));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToList());
            Instructions.Add(options?.Instructions);
            StreamingCalls++;
            IEnumerable<ChatResponseUpdate> updates = StreamingUpdates is null
                ? new ModelChatResponse(_responses.Dequeue()).ToChatResponseUpdates()
                : StreamingUpdates;
            var index = 0;
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                if (index++ == 0 && ContinueAfterFirstUpdate is not null)
                    await ContinueAfterFirstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

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
            bool mapOriginalLambda = false, string environmentName = "Testing")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environmentName,
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
