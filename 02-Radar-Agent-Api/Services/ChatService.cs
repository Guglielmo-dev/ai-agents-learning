using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using radar_agent_api.Contracts.Chat;
using radar_agent_api.Data;
using radar_agent_api.Store;
using ChatResponse = radar_agent_api.Contracts.Chat.ChatResponse;

namespace radar_agent_api.Services;

public sealed class ChatService(
    AIAgent agent,
    PersistentSessionStore sessionStore,
    AppDbContext dbContext)
{
    public async Task<ChatResponse> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
    {
        var session = await sessionStore.GetOrCreateAsync(agent, request.ConversationId, ct);
        var response = await agent.RunAsync(request.Message, session, cancellationToken: ct);

        await sessionStore.SaveAsync(agent, request.ConversationId, session, ct);

        return await HandleAgentResponseAsync(request.ConversationId, response);
    }

    public async Task<string> SendRawMessageAsync(ChatRequest request, CancellationToken ct = default)
    {
        var session = await sessionStore.GetOrCreateAsync(agent, request.ConversationId, ct);
        var response = await agent.RunAsync(request.Message, session, cancellationToken: ct);

        await sessionStore.SaveAsync(agent, request.ConversationId, session, ct);

        return response.Text;
    }

    public async Task<ChatApprovalResult> ApproveAsync(
        ApprovalDecisionRequest decision,
        CancellationToken ct = default)
    {
        var pending = await dbContext.PendingApprovals
            .FirstOrDefaultAsync(p => p.RequestId == decision.RequestId
                && p.ConversationId == decision.ConversationId, ct);

        if (pending is null)
            return new ChatApprovalResult(ChatApprovalStatus.NotFound);

        var requestContent = JsonSerializer.Deserialize<AIContent>(
            pending.SerializedRequestJson,
            AIJsonUtilities.DefaultOptions) as ToolApprovalRequestContent;

        if (requestContent is null)
            return new ChatApprovalResult(ChatApprovalStatus.InvalidStoredRequest);

        var session = await sessionStore.GetOrCreateAsync(agent, decision.ConversationId, ct);
        var approvalResponse = requestContent.CreateResponse(decision.Approved);
        var approvalMessage = new ChatMessage(ChatRole.User, [approvalResponse]);

        var response = await agent.RunAsync(approvalMessage, session, cancellationToken: ct);

        dbContext.PendingApprovals.Remove(pending);
        await dbContext.SaveChangesAsync(ct);
        await sessionStore.SaveAsync(agent, decision.ConversationId, session, ct);

        var result = await HandleAgentResponseAsync(decision.ConversationId, response);
        return new ChatApprovalResult(ChatApprovalStatus.Completed, result);
    }

    private async Task<ChatResponse> HandleAgentResponseAsync(string conversationId, AgentResponse response)
    {
        var approvalRequests = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList();

        if (approvalRequests.Count == 0)
            return new ChatResponse(conversationId, response.Text);

        var pendingDtos = new List<PendingApprovalDto>();

        foreach (var request in approvalRequests)
        {
            var functionCall = (FunctionCallContent)request.ToolCall;
            var serialized = JsonSerializer.Serialize<AIContent>(request, AIJsonUtilities.DefaultOptions);

            dbContext.PendingApprovals.Add(new PendingApproval
            {
                RequestId = request.RequestId,
                ConversationId = conversationId,
                FunctionName = functionCall.Name,
                SerializedRequestJson = serialized
            });

            pendingDtos.Add(new PendingApprovalDto(
                request.RequestId,
                functionCall.Name,
                functionCall.Arguments));
        }

        await dbContext.SaveChangesAsync();

        return new ChatResponse(
            conversationId,
            Reply: "Azione in attesa di approvazione umana",
            pendingDtos);
    }
}
