namespace radar_agent_api.Contracts.Chat
{
    public record PendingApprovalDto(
        string RequestId,
        string FunctionName,
        IDictionary<string, object?>? Arguments
    );
}
