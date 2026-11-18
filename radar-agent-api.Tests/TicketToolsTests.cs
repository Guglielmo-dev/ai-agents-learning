using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using radar_agent_api.Agents.Tools;
using radar_agent_api.Data;

namespace radar_agent_api.Tests;

public class TicketToolsTests
{
    // Helper: crea un database InMemory isolato e pulito per ogni test
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldSaveNewTicketWithOpenStatus()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        var tools = new TicketTools(dbContext);

        // Act
        var resultJson = await tools.CreateTicketAsync("Server posta bloccato", "Critical");

        // Assert
        var savedTicket = await dbContext.Tickets.SingleOrDefaultAsync();
        Assert.NotNull(savedTicket);
        Assert.Equal("Server posta bloccato", savedTicket.Title);
        Assert.Equal("Critical", savedTicket.Priority);
        Assert.Equal("Open", savedTicket.Status);
        Assert.Contains("Server posta bloccato", resultJson);
    }

    [Fact]
    public async Task SearchTicketsAsync_WithStatusOpen_ShouldReturnOnlyOpenTickets()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        dbContext.Tickets.AddRange(
            new Ticket { Title = "VPN down", Priority = "High", Status = "Open" },
            new Ticket { Title = "Cambio tastiera", Priority = "Low", Status = "Closed" },
            new Ticket { Title = "Errore SQL", Priority = "Critical", Status = "Open" }
        );
        await dbContext.SaveChangesAsync();

        var tools = new TicketTools(dbContext);

        // Act
        var resultJson = await tools.SearchTicketsAsync("Open");
        var tickets = JsonSerializer.Deserialize<List<Ticket>>(resultJson);

        // Assert
        Assert.NotNull(tickets);
        Assert.Equal(2, tickets.Count);
        Assert.All(tickets, t => Assert.Equal("Open", t.Status));
    }

    [Fact]
    public async Task SearchTicketsAsync_WhenNoTicketsMatch_ShouldReturnFriendlyMessage()
    {
        // Arrange
        await using var dbContext = CreateInMemoryDbContext();
        dbContext.Tickets.Add(new Ticket { Title = "Monitor rotto", Priority = "Medium", Status = "Open" });
        await dbContext.SaveChangesAsync();

        var tools = new TicketTools(dbContext);

        // Act
        var result = await tools.SearchTicketsAsync("Closed");

        // Assert
        Assert.Contains("Nessun ticket trovato", result);
    }
}