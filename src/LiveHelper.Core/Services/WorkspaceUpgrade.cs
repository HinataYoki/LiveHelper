using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public static class WorkspaceUpgrade
{
    public static bool Apply(PersistedState state)
    {
        var changed = false;
        foreach (var room in state.Rooms)
        {
            if (string.IsNullOrWhiteSpace(room.PlatformRoomId))
            {
                var roomId = PlatformDetector.TryExtractPlatformRoomId(room.Platform, room.Url);
                if (!string.IsNullOrWhiteSpace(roomId))
                {
                    room.PlatformRoomId = roomId;
                    changed = true;
                }
            }
        }

        if (state.Version < PersistedState.CurrentVersion)
        {
            state.Version = PersistedState.CurrentVersion;
            changed = true;
        }

        return changed;
    }
}
