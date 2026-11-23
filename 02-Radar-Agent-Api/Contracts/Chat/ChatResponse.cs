namespace radar_agent_api.Contracts.Chat
{
    public record ChatResponse(
        string ConversationId,
        string? Reply,
        List<PendingApprovalDto>? PendingApprovals = null
    );
}
