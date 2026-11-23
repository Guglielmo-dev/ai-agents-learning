# Chat structural refactoring implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Complete the user's Contracts/Chat and ChatEndpoints extraction while preserving existing HTTP and agent behavior.

**Architecture:** Keep one Minimal API project. ChatEndpoints translates HTTP requests/results; a scoped ChatService coordinates the existing agent, store, and database operations. AddRadarAgent registers the same scoped agent/tools and singleton client/store; agent instructions move verbatim to a separate file.

**Tech Stack:** .NET 10, Microsoft.Agents.AI 1.28.0, EF Core 10.0.15, OllamaSharp 5.5.2, xUnit.

**Spec:** User's approved continuation of the structural refactoring described in this chat, with the explicit constraint "senza rompere logiche esistenti".

## Global constraints

- Continue in the user's current checkout and preserve their CORS, package, DTO, endpoint, and UI changes. Do not commit or create a branch automatically.
- Preserve /chat, /chat/raw, /chat/approve, endpoint names, summaries, JSON shapes, error strings, prompt, model, timeout, DI lifetimes, and persistence order.
- No dependency upgrades, database/model/migration changes, validation changes, approval-policy changes, or cache/concurrency changes.
- Existing known behavioral concerns remain separate follow-up work.

## Review focus

- Approval continuation uses the same persisted session and the original server-side request content.
- Missing and invalid stored approvals retain the existing 404/500 responses.
- Raw responses stay plain text and retain the existing persistence behavior.
- New pending approvals keep the existing response shape and are persisted after the session.
- Tools and agents remain scoped while the chat client and session store remain singleton.

## Task 1: Restore a compiling baseline

- [x] Update stale contract imports in Program.cs and Agents/AgentHelpers.cs.
- [x] Run API build and the existing TicketTools tests; record the initial namespace failure and the restored baseline.

## Task 2: Extract and verify the application boundaries

Files: Endpoints/ChatEndpoints/ChatEndpoints.cs, Services/ChatService.cs, Services/ChatApprovalResult.cs, Extensions/AgentServiceCollectionExtensions.cs, Agents/SupportAgentInstructions.cs, Program.cs. Remove Agents/AgentHelpers.cs after moving its implementation.

Interfaces: MapChatEndpoints(IEndpointRouteBuilder) -> RouteGroupBuilder; AddRadarAgent(IServiceCollection, IConfiguration) -> IServiceCollection; ChatService.SendMessageAsync(ChatRequest, CancellationToken) -> Task<ChatResponse>; SendRawMessageAsync(ChatRequest, CancellationToken) -> Task<string>; ApproveAsync(ApprovalDecisionRequest, CancellationToken) -> Task<ChatApprovalResult>. ChatApprovalResult carries Completed, NotFound, or InvalidStoredRequest and an optional ChatResponse.

- [x] Add regression tests in the existing sibling test project for response contracts, approval/denial, missing/cross-conversation approvals, invalid stored content, raw behavior, session restoration, routes, and DI lifetimes. Use a deterministic IChatClient and EF InMemory; no Ollama/SQL dependency.
- [x] Confirm the new boundaries do not yet compile before implementing them.
- [x] Move the current orchestration and response handling verbatim into ChatService; map HTTP outcomes in ChatEndpoints.
- [x] Extract DI registration and instructions without changing defaults; wire Program.cs.
- [x] Run build and the full test suite. Compare moved logic against bin/refactor-baseline.

## Task 3: Documentation and final verification

- [x] Update README with the resulting file responsibilities and scope of this refactoring.
- [x] Obtain a read-only review, fix any introduced important issues, and run final build/tests.
- [x] Summarize changes and runtime verification limitations in README and the final handoff.

## Progress and rulings

- Initial build: fails with CS0234 because AgentHelpers still imports radar_agent_api.Data.ChatResponse after the user's DTO move.
- Ruling: work in place, as requested by the user who has already begun the refactoring; do not move their uncommitted work into a new worktree.
- Ruling: pure structural extraction uses characterization/regression tests; known approval, cache, and ticket normalization issues are preserved to avoid changing application behavior.
- Baseline restored: API build succeeds with 0 warnings/errors; all 8 existing tests pass.
- Regression tests initially fail to compile because Extensions/Services have not been extracted; after extraction, 18/19 pass. Route metadata test detects /chat becoming /chat/ through a prefixed route group; using an empty-prefix group and explicit existing paths restores 19/19.
- Final metadata compatibility: the original async Results handlers return Task<IResult>, without an explicit response-body schema. Preserve those return types. The regression test now compares ApiExplorer response types and effective tags against an original-style async lambda, instead of assuming a typed schema existed.
- Final review: fixed OpenAPI grouping by using the host application name; fixed added response-schema metadata by retaining IResult; restored the original CRLF separators in the raw-string prompt; verified the production IChatClient descriptor before substituting the test model.
- Review fixes verified RED -> GREEN: exact model-side prompt and original-style OpenAPI metadata comparisons failed before the fixes. Final suite: 20/20 passed (8 existing + 12 new cases).
- Final verification: `dotnet build radar-agent-api.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -v:minimal` succeeds with 0 warnings/errors; `dotnet test ../radar-agent-api.Tests/radar-agent-api.Tests.csproj --no-build --no-restore -v:minimal` passes 20/20; `git diff --check` succeeds.
- Verification limits: HTTP tests use Kestrel, a scripted IChatClient, and EF InMemory. SQL Server/Ollama integration and existing concurrency/approval-batching concerns are not verified or changed by this structural refactoring.
- No commits, dependency changes, migrations, or production configuration changes were made.
