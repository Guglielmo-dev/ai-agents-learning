namespace radar_agent_api.Data
{
    public class Ticket
    {
        public int Id { get; set; }
        public required string Title { get; set; }
        public string Status { get; set; } = "Open";
        public required string Priority { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // DTO per il body JSON in ingresso e in uscita
    public record ChatRequest(string ConversationId, string Message);

    public record PendingApprovalDto(
        string RequestId,
        string FunctionName,
        IDictionary<string, object?>? Arguments
    );

    public record ChatResponse(
        string ConversationId,
        string? Reply,
        List<PendingApprovalDto>? PendingApprovals = null
    );

    public record ApprovalDecisionRequest(
        string ConversationId,
        string RequestId,
        bool Approved
    );
}
