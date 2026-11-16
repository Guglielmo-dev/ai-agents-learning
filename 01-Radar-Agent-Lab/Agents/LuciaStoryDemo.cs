using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Radar_Agent_Lab.Agents
{
    public static class LuciaStoryDemo
    {
        public static async Task RunAsync(IChatClient chatClient)
        {
            AIAgent writer = chatClient.AsAIAgent(
                name: "Writer",
                instructions: "Scrivi storie coinvolgenti e creative"
            );

            var session = await writer.CreateSessionAsync();
            //AgentResponse response = await writer.RunAsync("Write a short story about a haunted house with a character named Lucia.");
            Console.WriteLine("sending request to Qwen3... \n");

            Console.WriteLine("--- Turno 1 ---");
            await foreach (var update in writer.RunStreamingAsync(
                "Inventa un personaggio di nome Lucia che esplora una casa infestata, in 3 frasi.",
                session))
            {
                Console.Write(update.Text);
            }

            Console.WriteLine("\n\n--- Turno 2 ---");
            await foreach (var update in writer.RunStreamingAsync(
                "Cosa vede Lucia dietro la prima porta che apre? Continua in 3 frasi.",
                session))
            {
                Console.Write(update.Text);
            }

            Console.WriteLine("\n\n--- End ---");

            //Console.WriteLine(response.Text);
        }
    }
}