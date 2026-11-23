namespace radar_agent_api.Data
{
    public class PendingApproval
    {
        public required string RequestId { get; set; }
        public required string ConversationId { get; set; }
        public required string FunctionName { get; set; }
        public required string SerializedRequestJson { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
