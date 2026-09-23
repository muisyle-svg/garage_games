namespace GarageGames.V2;

public enum MasterMode
{
    Idle,
    Speed
}

public sealed record MasterRunStatus(string State, int RemainingSeconds);

public sealed record MasterScanReply(string ScanId, string Kind, string? DeviceId, int? Count);

public sealed record MasterConnectionSnapshot(
    bool Connected,
    string? Port,
    IReadOnlyList<string> AvailablePorts,
    string? Mode,
    string? LastMessage);

public static class MasterProtocolCodec
{
    public const int MaximumLineLength = 128;

    public static bool IsValidBootToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 64 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsValidDeviceId(string? deviceId) =>
        deviceId is { Length: 12 } && deviceId.All(IsHexDigit);

    public static string GetStartMessageId(string bootToken, ulong sequence) =>
        $"master-start:{bootToken}:{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string FormatStatus(MasterRunStatus status) =>
        $"GG1 STATUS {status.State} {status.RemainingSeconds}";

    public static bool TryParseScanReply(string line, out MasterScanReply reply)
    {
        reply = null!;
        if (line.Length > MaximumLineLength)
        {
            return false;
        }

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 5 && parts[0] == "GG1" && parts[1] == "SCAN" && IsValidBootToken(parts[2]) &&
            parts[3] == "NODE" && IsValidDeviceId(parts[4]))
        {
            reply = new MasterScanReply(parts[2], "NODE", parts[4].ToUpperInvariant(), null);
            return true;
        }

        if (parts.Length == 5 && parts[0] == "GG1" && parts[1] == "SCAN" && IsValidBootToken(parts[2]) &&
            parts[3] == "DONE" && int.TryParse(parts[4], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0)
        {
            reply = new MasterScanReply(parts[2], "DONE", null, count);
            return true;
        }

        if (parts.Length == 4 && parts[0] == "GG1" && parts[1] == "SCAN" && IsValidBootToken(parts[2]) &&
            parts[3] == "BUSY")
        {
            reply = new MasterScanReply(parts[2], "BUSY", null, null);
            return true;
        }

        return false;
    }

    public static bool TryParseStartMessageId(string messageId, out string bootToken, out ulong sequence)
    {
        bootToken = "";
        sequence = 0;
        const string prefix = "master-start:";
        if (!messageId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = messageId[prefix.Length..];
        var separator = suffix.LastIndexOf(':');
        if (separator <= 0 || !IsValidBootToken(suffix[..separator]) ||
            !ulong.TryParse(suffix[(separator + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sequence))
        {
            return false;
        }

        bootToken = suffix[..separator];
        return true;
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    public static bool TryParseLine(string line, out string normalizedLine, out string kind,
        out string value, out ulong sequence)
    {
        normalizedLine = "";
        kind = "";
        value = "";
        sequence = 0;
        if (line.Length > MaximumLineLength)
        {
            return false;
        }

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || parts[0] != "GG1")
        {
            return false;
        }

        if (parts[1] == "HELLO" && parts.Length == 3 && IsValidBootToken(parts[2]))
        {
            kind = "HELLO";
            value = parts[2];
        }
        else if (parts[1] == "MODE" && parts.Length == 3 && parts[2] is "IDLE" or "SPEED")
        {
            kind = "MODE";
            value = parts[2];
        }
        else if (parts[1] == "START" && parts.Length == 4 && IsValidBootToken(parts[2]) &&
            ulong.TryParse(parts[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sequence))
        {
            kind = "START";
            value = parts[2];
        }
        else
        {
            return false;
        }

        normalizedLine = string.Join(' ', parts);
        return true;
    }
}

public sealed class MasterProtocolState
{
    public string? BootToken { get; private set; }
    public MasterMode? Mode { get; private set; }
    public string? LastMessage { get; private set; }

    public bool ProcessLine(string line, Func<string, ulong, bool, InputResult> receiveStart)
    {
        if (!MasterProtocolCodec.TryParseLine(line, out var normalized, out var kind, out var value, out var sequence))
        {
            return false;
        }

        LastMessage = normalized;
        switch (kind)
        {
            case "HELLO":
                BootToken = value;
                return true;
            case "MODE":
                Mode = value == "SPEED" ? MasterMode.Speed : MasterMode.Idle;
                return true;
            case "START":
                if (!string.Equals(BootToken, value, StringComparison.Ordinal))
                {
                    return true;
                }

                receiveStart(value, sequence, Mode != MasterMode.Speed);
                return true;
            default:
                return false;
        }
    }

    public void EnsureArmAllowed()
    {
        if (Mode == MasterMode.Speed)
        {
            throw new CommandException("Arming and starting are disabled while the physical master is in SPEED mode.");
        }
    }

    public void Reset()
    {
        BootToken = null;
        Mode = null;
        LastMessage = null;
    }
}
