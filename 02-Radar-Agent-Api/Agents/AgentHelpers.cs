using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using radar_agent_api.Data;
using System.Text.Json;
using ChatResponse = radar_agent_api.Data.ChatResponse;

namespace radar_agent_api.Agents
{
    public static class AgentHelpers
    {
        public static async Task<ChatResponse> HandleAgentResponseAsync(
            string conversationId,
            AgentResponse response,
            AppDbContext dbContext)
        {
            var approvalRequests = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<ToolApprovalRequestContent>()
                .ToList();

            if (approvalRequests.Count == 0)
            {
                return new ChatResponse(conversationId, response.Text);
            }

            var pendingDtos = new List<PendingApprovalDto>();

            foreach (var req in approvalRequests)
            {
                var functionCall = (FunctionCallContent)req.ToolCall;

                // Usiamo AIJsonUtilities.DefaultOptions per serializzare correttamente i tipi AIContent
                string serialized = JsonSerializer.Serialize<AIContent>(req, AIJsonUtilities.DefaultOptions);

                dbContext.PendingApprovals.Add(new PendingApproval
                {
                    RequestId = req.RequestId,
                    ConversationId = conversationId,
                    FunctionName = functionCall.Name,
                    SerializedRequestJson = serialized
                });

                pendingDtos.Add(new PendingApprovalDto(
                    req.RequestId,
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
}
