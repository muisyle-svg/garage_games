using System.Runtime.InteropServices;

namespace GarageGames.V2;

// While the app runs, Windows is asked not to sleep: sleeping mid-event closes the physical
// master's USB serial port and stops every button. The display may still turn off, and the
// request ends with the app. --allow-sleep turns this off.
internal static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    // Call from the thread that stays alive for the app's lifetime (the main thread).
    public static bool Request() =>
        OperatingSystem.IsWindows() && SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
}
