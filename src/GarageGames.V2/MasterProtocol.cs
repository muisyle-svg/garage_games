namespace GarageGames.V2;

public enum MasterMode
{
    Idle,
    Speed
}

public sealed record MasterRunStatus(string State, int RemainingSeconds);

public sealed record MasterGarageStatus(string Token, string State);

public sealed record MasterGarageEventStatus(string RunToken, int Revision, string DeviceId, string State);

public sealed record MasterGarageEventSnapshot(string? Version, IReadOnlyList<MasterGarageEventStatus> Events);

// The bonus round's state for the buttons: INTRO (poll and flash), TARGET (DeviceId lit with
// RemainingMs to go; null DeviceId means a virtual-only target), or OFF.
public sealed record MasterBonusStatus(string Token, int Sequence, string Phase, string? DeviceId, long RemainingMs);

// AgeMilliseconds is how long before the relay line the button was pressed (0 from older firmware).
public sealed record MasterPhysicalPress(string BootToken, string RunToken, string DeviceId, uint Sequence,
    uint AgeMilliseconds = 0);
public sealed record MasterButtonTestPress(string BootToken, string DeviceId, uint Sequence);

// A keypad spoke reports its typed entry (Submit = false) after each key so the TV can
// show it, and submits the entry when '*' is pressed. Only submissions are acknowledged.
public sealed record MasterKeypadInput(string BootToken, string RunToken, string DeviceId, uint Sequence,
    bool Submit, string Entry, uint AgeMilliseconds = 0);

public sealed record MasterPhysicalPressResult(string State, MessageDisposition Disposition, string Reason);

public sealed record MasterScanReply(string ScanId, string Kind, string? DeviceId, int? Count);

public sealed record MasterConnectionSnapshot(
    bool Connected,
    string? Port,
    IReadOnlyList<string> AvailablePorts,
    string? Mode,
    string? LastMessage,
    string? LastTestDeviceId = null,
    DateTimeOffset? LastTestAt = null);

public static class MasterProtocolCodec
{
    public const int MaximumLineLength = 128;
    // Bounded by the 63-byte ESP-NOW packet the spoke sends ("GKEY:3:<token>:<seq>:<S|K>:<entry>:<age>").
    public const int MaximumKeypadEntryLength = 12;

    // '*' is the keypad's Enter key, so it never appears inside an entry.
    public static bool IsValidKeypadEntry(string? entry) =>
        entry is not null && entry.Length <= MaximumKeypadEntryLength &&
        entry.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'D' or '#');

    public static bool IsValidBootToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 64 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsValidDeviceId(string? deviceId) =>
        deviceId is { Length: 12 } && deviceId.All(IsHexDigit);

    public static string GetStartMessageId(string bootToken, ulong sequence) =>
        $"master-start:{bootToken}:{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string GetPhysicalPressMessageId(string runToken, string deviceId, uint sequence) =>
        $"spoke-press:{runToken}:{deviceId}:{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string GetKeypadSubmitMessageId(string runToken, string deviceId, uint sequence) =>
        $"spoke-keypad:{runToken}:{deviceId}:{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

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

    public static string FormatGarageEventStatus(MasterGarageEventStatus status) =>
        $"GG1 EVENT {status.RunToken} {status.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)} {status.DeviceId.ToUpperInvariant()} {status.State}";

    public static string FormatPhysicalPressResult(MasterPhysicalPress press, string state) =>
        $"GG1 RESULT {press.RunToken} {press.DeviceId} {press.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)} {state}";

    public static string FormatBonusStatus(MasterBonusStatus status) =>
        $"GG1 BONUS {status.Token} {status.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)} {status.Phase} " +
        $"{status.DeviceId ?? "-"} {Math.Clamp(status.RemainingMs, 0, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // GG1 BONUSNODE <bootToken> <runToken> <mac>: a button answered the bonus round's poll.
    public static bool TryParseBonusPollReply(string line, out string bootToken, out string runToken, out string deviceId)
    {
        bootToken = runToken = deviceId = "";
        var parts = line.Length > MaximumLineLength ? [] : line.Split(' ');
        if (parts.Length != 5 || parts[0] != "GG1" || parts[1] != "BONUSNODE" || !IsValidBootToken(parts[2]) ||
            !IsUpperHex(parts[3], 16) || !IsUpperHex(parts[4], 12))
        {
            return false;
        }

        (bootToken, runToken, deviceId) = (parts[2], parts[3], parts[4]);
        return true;
    }

    public static string FormatKeypadResult(MasterKeypadInput input, string state) =>
        $"GG1 RESULT {input.RunToken} {input.DeviceId} {input.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)} {state}";

    public static string FormatButtonTest(MasterButtonTestPress press) =>
        $"GG1 TEST {press.BootToken} {press.DeviceId} {press.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string FormatIdentifyCommand(string deviceId, uint sequence) =>
        $"GG1 IDENTIFY {deviceId.ToUpperInvariant()} {sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static bool TryParseButtonTest(string line, out MasterButtonTestPress press)
    {
        press = null!;
        if (line.Length > MaximumLineLength) return false;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5 || parts[0] != "GG1" || parts[1] != "TEST" ||
            !IsValidBootToken(parts[2]) || !IsUpperHex(parts[3], 12) ||
            !uint.TryParse(parts[4], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence == 0)
        {
            return false;
        }

        press = new MasterButtonTestPress(parts[2], parts[3], sequence);
        return true;
    }

    public static bool TryParsePhysicalPress(string line, out MasterPhysicalPress press)
    {
        press = null!;
        if (line.Length > MaximumLineLength)
        {
            return false;
        }

        var parts = line.Split(' ');
        if (parts.Length is not (6 or 7) || parts[0] != "GG1" || parts[1] != "PRESS" ||
            !IsValidBootToken(parts[2]) || !IsUpperHex(parts[3], 16) || !IsUpperHex(parts[4], 12) ||
            !TryParseDecimalUInt(parts[5], out var sequence) || sequence == 0)
        {
            return false;
        }

        var ageMilliseconds = 0u;
        if (parts.Length == 7 && !TryParseDecimalUInt(parts[6], out ageMilliseconds))
        {
            return false;
        }

        press = new MasterPhysicalPress(parts[2], parts[3], parts[4], sequence, ageMilliseconds);
        return true;
    }

    // GG1 KEYPAD <bootToken> <runToken> <mac> <sequence> <S|K> <entry or -> <ageMs>
    public static bool TryParseKeypadInput(string line, out MasterKeypadInput input)
    {
        input = null!;
        if (line.Length > MaximumLineLength)
        {
            return false;
        }

        var parts = line.Split(' ');
        if (parts.Length != 9 || parts[0] != "GG1" || parts[1] != "KEYPAD" ||
            !IsValidBootToken(parts[2]) || !IsUpperHex(parts[3], 16) || !IsUpperHex(parts[4], 12) ||
            !TryParseDecimalUInt(parts[5], out var sequence) || sequence == 0 ||
            parts[6] is not ("S" or "K") || !TryParseDecimalUInt(parts[8], out var ageMilliseconds))
        {
            return false;
        }

        var entry = parts[7] == "-" ? "" : parts[7];
        if (entry.Length == 0 && parts[7] != "-" || !IsValidKeypadEntry(entry))
        {
            return false;
        }

        input = new MasterKeypadInput(parts[2], parts[3], parts[4], sequence, parts[6] == "S", entry, ageMilliseconds);
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

    private static bool TryParseDecimalUInt(string value, out uint result)
    {
        result = 0;
        return value.Length > 0 && value.All(character => character is >= '0' and <= '9') &&
            uint.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result);
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
        => ProcessLine(line, receiveStart, receivePhysicalPress, null);

    public bool ProcessLine(string line, Func<string, ulong, bool, InputResult> receiveStart,
        Func<MasterPhysicalPress, bool, MasterPhysicalPressResult>? receivePhysicalPress,
        Func<MasterKeypadInput, bool, MasterPhysicalPressResult?>? receiveKeypad)
    {
        if (MasterProtocolCodec.TryParseKeypadInput(line, out var keypad))
        {
            LastMessage = line;
            var sessionAllowed = string.Equals(BootToken, keypad.BootToken, StringComparison.Ordinal) && Mode == MasterMode.Idle;
            receiveKeypad?.Invoke(keypad, sessionAllowed);
            return true;
        }

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
