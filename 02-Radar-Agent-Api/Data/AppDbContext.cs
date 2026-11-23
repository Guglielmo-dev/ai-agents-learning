using Microsoft.EntityFrameworkCore;

namespace radar_agent_api.Data
{
        public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
        {
            public DbSet<Ticket> Tickets => Set<Ticket>();
            public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
            public DbSet<PendingApproval> PendingApprovals => Set<PendingApproval>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                base.OnModelCreating(modelBuilder);
                modelBuilder.Entity<PendingApproval>().HasKey(p => p.RequestId);
            }
        }
}
