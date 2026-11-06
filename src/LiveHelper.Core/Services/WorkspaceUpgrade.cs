using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public static class WorkspaceUpgrade
{
    private const string LegacyRoomIdOnlyFolderTemplate = "{platform}_{roomId}";

    public static bool Apply(PersistedState state)
    {
        var changed = false;
        if (state.Version < 3
            && string.Equals(
                state.Settings.FolderTemplate,
                LegacyRoomIdOnlyFolderTemplate,
                StringComparison.OrdinalIgnoreCase))
        {
            state.Settings.FolderTemplate = AppSettings.DefaultFolderTemplate;
            changed = true;
        }

        if (state.Version < 4
            && string.Equals(
                state.Settings.RecordingFormat,
                "mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            // Recording through FLV keeps the original stream untouched. MP4 is
            // now an explicit post-recording conversion instead of a live transcode.
            state.Settings.RecordingFormat = AppSettings.DefaultRecordingFormat;
            changed = true;
        }

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
