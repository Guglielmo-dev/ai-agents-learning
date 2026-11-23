namespace radar_agent_api.Contracts.Chat
{
    public record ApprovalDecisionRequest(
        string ConversationId,
        string RequestId,
        bool Approved
    );
}
