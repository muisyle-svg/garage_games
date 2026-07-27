using GarageGames.Core.Domain;

namespace GarageGames.Core.State;

public static class RunLifecycle
{
    public static bool RequiresPersistedTimeout(RunSnapshot? run) =>
        run is
        {
            Status: RunStatus.Active or RunStatus.Bonus or RunStatus.TimedOut,
            RemainingSeconds: 0
        };
}
