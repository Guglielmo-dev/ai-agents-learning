using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using radar_agent_api.Agents;
using radar_agent_api.Agents.Tools;
using radar_agent_api.Services;
using radar_agent_api.Store;
using radar_agent_api.Telemetry;

namespace radar_agent_api.Extensions;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddRadarAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var ollamaUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434/";

        services.AddScoped<TicketTools>();
        services.AddScoped<ChatService>();
        services.AddSingleton<PersistentSessionStore>();

        services.AddSingleton<IChatClient>(_ =>
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(ollamaUrl),
                Timeout = TimeSpan.FromMinutes(5)
            };

            return new OllamaApiClient(httpClient, "qwen3:4b-instruct-2507-q4_K_M");
        });

        // L'agente usa tool con un DbContext scoped: ne condivide il lifetime.
        services.AddScoped<AIAgent>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var ticketTools = sp.GetRequiredService<TicketTools>();
            var environment = sp.GetRequiredService<IHostEnvironment>();

            AIFunction searchTool = AIFunctionFactory.Create(ticketTools.SearchTicketsAsync, name: "SearchTickets");
            AIFunction createTool = AIFunctionFactory.Create(ticketTools.CreateTicketAsync, name: "CreateTicket");
            AIFunction approvalRequiredCreateTicket = new ApprovalRequiredAIFunction(createTool);

            var agent = chatClient.AsAIAgent(
                name: "DatabaseSupportAgent",
                instructions: SupportAgentInstructions.Text,
                tools: [searchTool, approvalRequiredCreateTicket]);

            return agent
                .AsBuilder()
                .UseOpenTelemetry(
                    sourceName: AgentTelemetry.SourceName,
                    configure: telemetry => telemetry.EnableSensitiveData = environment.IsDevelopment())
                .Build();
        });

        return services;
    }
}
