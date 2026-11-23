using Microsoft.EntityFrameworkCore;
using radar_agent_api.Data;
using System.ComponentModel;
using System.Text.Json;

namespace radar_agent_api.Agents.Tools
{
    public class TicketTools(AppDbContext dbContext)
    {
        private static readonly string[] AllowedPriorities = ["Low", "Medium", "High", "Critical"];


        [Description("Cerca e restituisce i ticket di supporto dal database filtrati per stato (es. 'Open', 'Closed'). Se status è vuoto, restituisce tutti i ticket.")]
        public async Task<string> SearchTicketsAsync(
            [Description("Lo stato del ticket da cercare, ad esempio 'Open' oppure 'Closed'.")] string? status = null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[TOOL DB - SearchTickets]: Ricerca ticket con stato = '{status ?? "TUTTI"}'...");
            Console.ResetColor();

            var query = dbContext.Tickets.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(t => t.Status.ToLower() == status.Trim().ToLower());
            }

            var results = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();

            if (results.Count == 0)
                return $"Nessun ticket trovato con stato '{status}'.";

            return JsonSerializer.Serialize(results);
        }

        
        public async Task<string> CreateTicketAsync(
            [Description("Il titolo o breve descrizione del problema del ticket.")] string title,
            [Description("La priorità del ticket: 'Low', 'Medium', 'High' o 'Critical'.")] string priority)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "Errore di validazione: il titolo del ticket non può essere vuoto.";
            }

            var normalizedPriority = AllowedPriorities.FirstOrDefault(
                p => p.Equals(priority?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (normalizedPriority is null)
            {
                return $"Errore di validazione: la priorità '{priority}' non è valida. Valori ammessi: Low, Medium, High o Critical.";
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[TOOL DB - CreateTickets]: Creazione ticket '{title}' (Priorità: {priority})...");
            Console.ResetColor();

            var newTicket = new Ticket
            {
                Title = title,
                Priority = priority,
                Status = "Open",
            };

            dbContext.Tickets.Add(newTicket);
            await dbContext.SaveChangesAsync();

            return JsonSerializer.Serialize(new
            {
                Message = "Ticket creato con successo nel database.",
                Ticket = newTicket
            });
        }
    }
}
