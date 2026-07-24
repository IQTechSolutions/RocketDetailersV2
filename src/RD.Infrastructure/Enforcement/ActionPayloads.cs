namespace RD.Infrastructure.Enforcement;

// Outbox payloads (JSON in OutboxAction.PayloadJson). Small, explicit records —
// the dispatcher deserializes the shape matching the action type.

public sealed record CampaignActionPayload(IReadOnlyList<string> CampaignIds);

public sealed record GhlFieldPayload(string LocationId, string ContactId, string FieldKey, string Value, Guid DunningCaseId, int Step);

public sealed record GhlWorkflowPayload(string LocationId, string ContactId, string WorkflowId, Guid DunningCaseId, int Step);

public sealed record VerifyDeliveryPayload(Guid DunningCaseId, int Step, string ContactId, DateTimeOffset TriggeredAt);
