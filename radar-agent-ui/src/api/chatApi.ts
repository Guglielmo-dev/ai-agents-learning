const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

export interface ChatRequest {
  conversationId: string;
  message: string;
}

export interface PendingApprovalDto {
  requestId: string;
  functionName: string;
  arguments?: Record<string, unknown> | null;
}

export interface ChatResponse {
  conversationId: string;
  reply: string | null;
  pendingApprovals?: PendingApprovalDto[] | null;
}

export interface ApprovalDecisionRequest {
  conversationId: string;
  requestId: string;
  approved: boolean;
}

export async function sendChatMessage(payload: ChatRequest): Promise<ChatResponse> {
  const res = await fetch(`${API_BASE_URL}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });

  if (!res.ok) {
    const errorText = await res.text();
    throw new Error(errorText || `Errore HTTP ${res.status}`);
  }

  return res.json() as Promise<ChatResponse>;
}

export async function sendApprovalDecision(
  payload: ApprovalDecisionRequest
): Promise<ChatResponse> {
  const res = await fetch(`${API_BASE_URL}/chat/approve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });

  if (!res.ok) {
    const errorText = await res.text();
    throw new Error(errorText || `Errore HTTP ${res.status}`);
  }

  return res.json() as Promise<ChatResponse>;
}