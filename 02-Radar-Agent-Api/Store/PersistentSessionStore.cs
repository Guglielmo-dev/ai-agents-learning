using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using radar_agent_api.Data;
using System.Collections.Concurrent;
using System.Text.Json;

namespace radar_agent_api.Store
{
    public sealed class PersistentSessionStore(IServiceScopeFactory scopeFactory)
    {
        // Cache L1 in memoria thread-safe
        private readonly ConcurrentDictionary<string, AgentSession> _cache = new();
        
        public async Task<AgentSession> GetOrCreateAsync(
            AIAgent agent,
            string conversationId,
            CancellationToken ct = default)
        {
            AgentSession session;

            // 1. Hit in memoria: restituisce subito la sessione attiva
            if (_cache.TryGetValue(conversationId, out var cachedSession))
            {
                return cachedSession;
            }

            //2.Miss in memoria(es.dopo docker compose restart): cerca su SQL
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var record = await db.AgentConversations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);

            if (record is not null)
            {
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(record.StateJson);
                session = await agent.DeserializeSessionAsync(jsonElement, cancellationToken: ct);
            }
            else
            {
                session = await agent.CreateSessionAsync(ct);
            }

            _cache[conversationId] = session;
            return session;
        }

        public async Task SaveAsync(
            AIAgent agent,
            string conversationId,
            AgentSession session,
            CancellationToken ct = default)
        {
            // Aggiorna la cache L1
            _cache[conversationId] = session;

            // Serializza in modo asincrono (evita SerializeSession sincrono)
            JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
            string rawJson = serialized.GetRawText();

            // Write-through su SQL (Upsert)
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await db.AgentConversations
                .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);

            if (existing is null)
            {
                db.AgentConversations.Add(new AgentConversation
                {
                    ConversationId = conversationId,
                    StateJson = rawJson,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.StateJson = rawJson;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
