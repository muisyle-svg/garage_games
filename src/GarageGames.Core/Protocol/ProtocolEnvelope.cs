using GarageGames.Core.Domain;

namespace GarageGames.Core.Protocol;

public static class Protocol
{
    public const int Version = 1;
    public const int DefaultDiscoveryPort = 20260;
    public const int DefaultControllerPort = 5260;
    public const int MaximumStations = 20;
}

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string EventId,
    string? RunId,
    string DeviceId,
    string BootId,
    long Sequence,
    string Type,
    long ElapsedMilliseconds,
    object? Payload,
    string? AcknowledgesEventId = null)
{
    public static ProtocolEnvelope Ack(ProtocolEnvelope source) =>
        new(
            Protocol.Version,
            Guid.NewGuid().ToString("N"),
            source.RunId,
            "controller",
            Environment.MachineName,
            0,
            "acknowledgement",
            source.ElapsedMilliseconds,
            null,
            source.EventId);

    public DeviceEvent ToDeviceEvent(DateTimeOffset receivedAt) =>
        new(
            EventId,
            RunId ?? "",
            DeviceId,
            BootId,
            Sequence,
            Type,
            ElapsedMilliseconds,
            receivedAt,
            Payload is null ? "{}" : JsonDefaults.Serialize(Payload));
}

public sealed record ControllerCommand(
    int ProtocolVersion,
    string CommandId,
    string RunId,
    string Type,
    string TargetDeviceId,
    long ElapsedMilliseconds,
    object? Payload);

public sealed record DeviceHealthPayload(
    string FirmwareVersion,
    int BatteryMillivolts,
    int Rssi,
    int RadioFailures,
    string StationModule,
    int Channel);

