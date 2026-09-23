using System.Text.Json.Serialization;

namespace GarageGames.V2;

public sealed record AddCompetitorRequest(string Name);
public sealed record StartCompetitorRunRequest(string CompetitorId, RunCategory Category = RunCategory.Official);
public sealed record AddQueueRequest(string CompetitorId, RunCategory Category, bool ReplaceExistingOfficial = false, string? Reason = null);
public sealed record ReorderQueueRequest(List<string> QueueIds);
public sealed record ArmRequest(bool ManualOfflineOverride = false);
public sealed record AvailabilityRequest(DeviceAvailability Availability, string? Error = null);
public sealed record AdvanceClockRequest(long Milliseconds);
public sealed record ConnectMasterRequest(string Port);
public sealed record UndoRequest(long EditId, int ExpectedRevision, string Reason);
public sealed record ActionReasonRequest(string? Reason = null);
public sealed record CountdownFinishedRequest(string RunId);
public sealed record RunCountdownState(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RunStatus? Status);
public sealed record ClearDatabaseRequest(string ConfirmationPhrase);
