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
}
