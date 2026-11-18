using Microsoft.EntityFrameworkCore;

namespace radar_agent_api.Data
{
        public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
        {
            public DbSet<Ticket> Tickets => Set<Ticket>();
        }
}
