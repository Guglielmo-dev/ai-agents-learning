import { useState, type FormEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import {
  sendApprovalDecision,
  type PendingApprovalDto,
} from './api/chatApi';
import './App.css';

interface UiMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  pendingApprovals?: PendingApprovalDto[] | null;
}

export default function App() {
  const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';
  // Stesso conversationId per tutta la durata della conversazione
  const [conversationId, setConversationId] = useState<string>(() =>
    crypto.randomUUID()
  );
  const [input, setInput] = useState('');
  const [messages, setMessages] = useState<UiMessage[]>([]);
  const [handledRequests, setHandledRequests] = useState<Set<string>>(
    () => new Set()
  );
  const [isStreaming, setIsStreaming] = useState(false);
  const [streamError, setStreamError] = useState<string | null>(null);

  // Mutation 2: POST /chat/approve (integra il flusso HITL)
  const approveMutation = useMutation({
    mutationFn: sendApprovalDecision,
    onSuccess: (data, variables) => {
      setHandledRequests((prev) => new Set(prev).add(variables.requestId));
      setMessages((prev) => [
        ...prev,
        {
          id: crypto.randomUUID(),
          role: 'assistant',
          text: data.reply ?? '(Operazione completata)',
          pendingApprovals: data.pendingApprovals,
        },
      ]);
    },
  });

  const isBusy = isStreaming || approveMutation.isPending;

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const trimmed = input.trim();
    if (!trimmed || isBusy) return;

    const assistantMsgId = crypto.randomUUID();

    setMessages((prev) => [
      ...prev,
      { id: crypto.randomUUID(), role: 'user', text: trimmed },
      { id: assistantMsgId, role: 'assistant', text: '' },
    ]);
    setInput('');
    setStreamError(null);
    setIsStreaming(true); 

    const params = new URLSearchParams({
      conversationId,
      message: trimmed,
    });

    const eventSource = new EventSource(`${API_BASE_URL}/chat/stream?${params.toString()}`);

    // Ogni token arrivato viene accodato in tempo reale
    eventSource.addEventListener('chunk', (event) => {
      const chunk = (event as MessageEvent<string>).data;
      setMessages((prev) =>
        prev.map((m) =>
          m.id === assistantMsgId ? { ...m, text: m.text + chunk } : m
        )
      );
    });

      // Se l'agente chiede approvazione per CreateTicket
    eventSource.addEventListener('approval', (event) => {
      const approvals = JSON.parse((event as MessageEvent<string>).data) as PendingApprovalDto[];
      setMessages((prev) =>
        prev.map((m) =>
          m.id === assistantMsgId
            ? {
                ...m,
                text: m.text || 'Azione in attesa di approvazione umana.',
                pendingApprovals: approvals,
              }
            : m
        )
      );
    });

    // Fine dello stream: chiudi la connessione (altrimenti EventSource ritenta all'infinito!)
    eventSource.addEventListener('done', () => {
      eventSource.close();
      setIsStreaming(false);
    });

    eventSource.onerror = () => {
      eventSource.close();
      setIsStreaming(false);
      setStreamError('Connessione streaming interrotta.');
    };
  };

  const handleApproval = (requestId: string, approved: boolean) => {
    if (isBusy) return;
    approveMutation.mutate({ conversationId, requestId, approved });
  };

  const handleNewChat = () => {
    setConversationId(crypto.randomUUID());
    setMessages([]);
    setHandledRequests(new Set());
    setStreamError(null);
    approveMutation.reset();
  };

  return (
    <div className="chat-container">
      <header className="chat-header">
        <div>
          <h1>Radar Support Agent</h1>
          <small>Sessione: {conversationId}</small>
        </div>
        <button type="button" onClick={handleNewChat} disabled={isBusy}>
          Nuova chat
        </button>
      </header>

      <main className="chat-history">
        {messages.length === 0 && (
          <p className="empty-state">
            Scrivi un messaggio per iniziare (es. "Quali ticket sono aperti?" o
            "Apri un ticket High per la stampante").
          </p>
        )}

        {messages.map((msg) => (
          <div key={msg.id} className={`message ${msg.role}`}>
            <strong>{msg.role === 'user' ? 'Tu' : 'Agente'}</strong>
            <p>{msg.text}</p>

            {msg.pendingApprovals && msg.pendingApprovals.length > 0 && (
              <div className="approval-list">
                {msg.pendingApprovals.map((req) => {
                  const alreadyHandled = handledRequests.has(req.requestId);
                  return (
                    <div key={req.requestId} className="approval-card">
                      <div>
                        <strong>Tool richiesto:</strong> <code>{req.functionName}</code>
                      </div>
                      <pre>{JSON.stringify(req.arguments, null, 2)}</pre>
                      {!alreadyHandled ? (
                        <div className="approval-actions">
                          <button
                            type="button"
                            className="btn-approve"
                            disabled={isBusy}
                            onClick={() => handleApproval(req.requestId, true)}
                          >
                            Approva
                          </button>
                          <button
                            type="button"
                            className="btn-reject"
                            disabled={isBusy}
                            onClick={() => handleApproval(req.requestId, false)}
                          >
                            Rifiuta
                          </button>
                        </div>
                      ) : (
                        <small>Decisione inviata.</small>
                      )}
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        ))}

        {isBusy && <div className="status-indicator">L'agente sta elaborando...</div>}

        {(streamError || approveMutation.isError) && (
          <div className="error-box">
            Errore: {streamError ?? approveMutation.error?.message}
          </div>
        )}
      </main>

      <form className="chat-input-form" onSubmit={handleSubmit}>
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Scrivi un messaggio all'agente..."
          disabled={isBusy}
        />
        <button type="submit" disabled={isBusy || !input.trim()}>
          Invia
        </button>
      </form>
    </div>
  );
}