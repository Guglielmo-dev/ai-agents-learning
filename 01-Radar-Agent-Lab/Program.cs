using Microsoft.Extensions.AI;
using OllamaSharp;
using Radar_Agent_Lab.Agents;

var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:11434/"),
    Timeout = TimeSpan.FromMinutes(5)
};

IChatClient chatClient = new OllamaApiClient(
        httpClient,
        "qwen3:4b-instruct-2507-q4_K_M"
    );

Console.WriteLine("Scegli quale agente avviare:");
Console.WriteLine("1 - Agente Scrittore (Storia di Lucia)");
Console.WriteLine("2 - Agente Supporto IT (Task GetOpenTickets)");
Console.Write("\nScelta: ");

string? scelta = Console.ReadLine();

switch (scelta)
{
    case "1":
        await LuciaStoryDemo.RunAsync(chatClient);
        break;
    case "2":
        await TicketAgentDemo.RunAsync(chatClient);
        break;
    default:
        Console.WriteLine("Scelta non valida.");
        break;
}