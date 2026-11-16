using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;

namespace Radar_Agent_Lab.Agents
{
    public static class TicketAgentDemo
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
        public static async Task RunAsync(IChatClient chatClient)
        {
            //AIFunctionFactory è un wrapper che trasforma il metodo per renderlo disponibile all'agent
            AIFunction getOpenTicketsTool = AIFunctionFactory.Create(GetOpenTickets);

            AIAgent ticketAgent = chatClient.AsAIAgent(
                name: "TicketAgent",
                instructions: "Sei un assistente IT. Usa sempre il tool GetOpenTickets per controllare i ticket aperti prima di rispondere.",
                tools: [getOpenTicketsTool]                
            );

            Console.WriteLine("Chiedo all'agente quali ticket sono aperti...\n");

            AgentResponse resp = await ticketAgent.RunAsync(
                "Ciao! Puoi dirmi quali ticket sono attualmente aperti e quale ha la priorità più alta?");
           
            Console.WriteLine($"\nRisposta dell'agente: {resp.Text}");
        }
    }

    public record Ticket(string Id, string Titolo, string Stato, string Priorita);
}
