using System.ComponentModel.DataAnnotations;

namespace radar_agent_api.Data
{
    public class AgentConversation
    {
        [Key]
        [MaxLength(128)]
        public string ConversationId { get; set; } = default!;
        public string StateJson { get; set; } = default!;
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
