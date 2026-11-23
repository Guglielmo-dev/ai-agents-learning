using radar_agent_api.Contracts.Chat;

namespace radar_agent_api.Services;

public enum ChatApprovalStatus
{
    Completed,
    NotFound,
    InvalidStoredRequest
}

public sealed record ChatApprovalResult(
    ChatApprovalStatus Status,
    ChatResponse? Response = null);
