namespace GarageGames.V2;

public enum MasterMode
{
    Idle,
    Speed
}

public sealed record MasterRunStatus(string State, int RemainingSeconds);

public sealed record MasterGarageStatus(string Token, string State);

public sealed record MasterPhysicalPress(string BootToken, string RunToken, string DeviceId, uint Sequence);

public sealed record MasterPhysicalPressResult(string State, MessageDisposition Disposition, string Reason);

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

    public static string GetPhysicalPressMessageId(string runToken, string deviceId, uint sequence) =>
        $"spoke-press:{runToken}:{deviceId}:{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string? GetGarageRunToken(string? runId)
    {
        const string prefix = "run-";
        if (runId is null || !runId.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(runId[prefix.Length..], "N", out var runGuid))
        {
            return null;
        }

        return runGuid.ToString("N")[..16].ToUpperInvariant();
    }

    public static string FormatStatus(MasterRunStatus status) =>
        $"GG1 STATUS {status.State} {status.RemainingSeconds}";

    public static string FormatGarageStatus(MasterGarageStatus status) =>
        $"GG1 GARAGE {status.Token} {status.State}";

    public static string FormatPhysicalPressResult(MasterPhysicalPress press, string state) =>
        $"GG1 RESULT {press.RunToken} {press.DeviceId} {press.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)} {state}";

    public static bool TryParsePhysicalPress(string line, out MasterPhysicalPress press)
    {
        press = null!;
        if (line.Length > MaximumLineLength)
        {
            return false;
        }

        var parts = line.Split(' ');
        if (parts.Length != 6 || parts[0] != "GG1" || parts[1] != "PRESS" ||
            !IsValidBootToken(parts[2]) || !IsUpperHex(parts[3], 16) || !IsUpperHex(parts[4], 12) ||
            parts[5].Length == 0 || parts[5].Any(character => character is < '0' or > '9') ||
            !uint.TryParse(parts[5], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence == 0)
        {
            return false;
        }

        press = new MasterPhysicalPress(parts[2], parts[3], parts[4], sequence);
        return true;
    }

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

    private static bool IsUpperHex(string value, int length) =>
        value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

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
        => ProcessLine(line, receiveStart, null);

    public bool ProcessLine(string line, Func<string, ulong, bool, InputResult> receiveStart,
        Func<MasterPhysicalPress, bool, MasterPhysicalPressResult>? receivePhysicalPress)
    {
        if (MasterProtocolCodec.TryParsePhysicalPress(line, out var press))
        {
            LastMessage = line;
            var sessionAllowed = string.Equals(BootToken, press.BootToken, StringComparison.Ordinal) && Mode == MasterMode.Idle;
            receivePhysicalPress?.Invoke(press, sessionAllowed);
            return true;
        }

        if (!MasterProtocolCodec.TryParseLine(line, out var normalized, out var kind, out var value, out var sequence))
        {
            return false;
        }

        LastMessage = normalized;
        switch (kind)
        {
            case "HELLO":
                if (!string.Equals(BootToken, value, StringComparison.Ordinal))
                {
                    Mode = null;
                }
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
