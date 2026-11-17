using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;

namespace radar_agent_api.Agents
{
    public static class TicketAgentFactory
    {
        [Description("Recupera l'elenco di tutti i ticket di supporto attualmente aperti dal file JSON.")]
        public static string GetOpenTickets()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[TOOL ESEGUITO]: Sto leggendo i ticket aperti da tickets.json...");
            Console.ResetColor();

            if (!File.Exists("tickets.json"))
                return "Nessun file tickets.json trovato.";

            string json = File.ReadAllText("tickets.json");
            var tickets = JsonSerializer.Deserialize<List<Ticket>>(json) ?? [];
            var openTickets = tickets.Where(t => t.Stato.Equals("Aperto", StringComparison.OrdinalIgnoreCase)).ToList();

            return JsonSerializer.Serialize(openTickets);
        }

        public static AIAgent Create(IChatClient chatClient)
        {
            AIFunction getOpenTicketsTool = AIFunctionFactory.Create(GetOpenTickets);

            return chatClient.AsAIAgent(
                    name: "SupportAgent",
                    instructions: "Sei un assistente IT aziendale. Usa sempre il tool GetOpenTickets per controllare i ticket aperti prima di rispondere alle domande sui ticket.",
                    tools: [getOpenTicketsTool]
                );
        }
    }

    public record Ticket(
        string Id,
        string Titolo,
        string Stato,
        string Priorita,
        string AssegnatoA
    );
}
