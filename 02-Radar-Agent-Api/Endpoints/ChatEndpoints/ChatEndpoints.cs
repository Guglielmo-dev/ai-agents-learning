using radar_agent_api.Contracts.Chat;
using radar_agent_api.Services;

namespace radar_agent_api.Endpoints.ChatEndpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var applicationName = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().ApplicationName;
        var group = endpoints.MapGroup(string.Empty).WithTags(applicationName);

        group.MapPost("/chat", SendMessageAsync)
            .WithName("ChatWithAgent")
            .WithSummary("Invia un messaggio all'agente di supporto mantenendo lo stato tramite conversationId");

        group.MapPost("/chat/raw", SendRawMessageAsync);

        group.MapPost("/chat/approve", ApproveAsync)
            .WithName("ApproveRequest")
            .WithSummary("Approva o rifiuta una richiesta di approvazione");

        return group;
    }

    private static async Task<IResult> SendMessageAsync(
        ChatRequest request,
        ChatService chatService,
        CancellationToken ct)
    {
        var response = await chatService.SendMessageAsync(request, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> SendRawMessageAsync(
        ChatRequest request,
        ChatService chatService,
        CancellationToken ct)
    {
        var response = await chatService.SendRawMessageAsync(request, ct);
        return Results.Text(response, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> ApproveAsync(
        ApprovalDecisionRequest decision,
        ChatService chatService,
        CancellationToken ct)
    {
        var result = await chatService.ApproveAsync(decision, ct);

        return result.Status switch
        {
            ChatApprovalStatus.Completed => Results.Ok(result.Response),
            ChatApprovalStatus.NotFound => Results.NotFound("Richiesta di approvazione non trovata o già gestita."),
            ChatApprovalStatus.InvalidStoredRequest => Results.Problem("Impossibile deserializzare la richiesta di approvazione."),
            _ => throw new InvalidOperationException("Esito dell'approvazione non riconosciuto.")
        };
    }
}
