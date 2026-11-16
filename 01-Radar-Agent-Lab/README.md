# radar-agent-lab

A minimal AI agent laboratory in .NET 10 built with **Microsoft Agent Framework**, running 100% locally with **Ollama** (`qwen3:4b`).

## What it does

This console application provides an interactive menu to run two different AI agent demos powered by a local LLM:

1. **Creative Writer Agent (Conversation Memory / Sessions):** A storytelling agent that generates a multi-turn story about a character named Lucia exploring a haunted house, maintaining conversation history and context across turns using `AgentSession`.
2. **IT Support Agent (Tool Calling):** An assistant that answers questions about open support tickets by automatically invoking a C# tool (`GetOpenTickets`) via `AIFunctionFactory`, which reads and filters mock ticket data from `tickets.json`.

## Tech stack

* **.NET 10** (Console App)
* **Microsoft Agent Framework** (`Microsoft.Agents.AI`)
* **Microsoft.Extensions.AI** (`IChatClient`, `AIFunctionFactory`)
* **OllamaSharp** (`OllamaApiClient`)
* **Ollama** (`qwen3:4b-instruct-2507-q4_K_M`)

## How to run

1. Install [Ollama](https://ollama.com/) and pull the model locally:
   ```bash
   ollama pull qwen3:4b-instruct-2507-q4_K_M

## Note
1. Make sure Ollama is running in the background (http://localhost:11434).
2. Run the .NET project from the root directory: dotnet run
3. Select 1 for the Creative Writer Agent or 2 for the IT Support Ticket Agent.

## What I learned
* **Provider-agnostic Agents** (IChatClient): Microsoft Agent Framework decouples agent logic from the underlying AI provider, making it seamless to swap cloud models (like Azure OpenAI) for local LLMs via OllamaSharp without rewriting agent behaviors.
* **Conversation State** (AgentSession): Stateless LLM calls become stateful multi-turn conversations by passing an AgentSession across runs, allowing the agent to recall prior context (like character details in the story demo).
* **Tool Calling** (AIFunctionFactory): Wrapping standard C# methods with AIFunctionFactory.Create and [Description] attributes enables the model to autonomously decide when to execute local code and ground its answers in real external data (tickets.json).