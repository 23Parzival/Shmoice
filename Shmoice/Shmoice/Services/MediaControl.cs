using Windows.Media.Control;

namespace Shmoice.Services;

/// <summary>
/// Real Play and Pause through Windows' media sessions (the same ones the volume flyout shows),
/// instead of the Play/Pause key, which only toggles. So "play" never pauses something, and
/// "pause" never starts something.
/// </summary>
public static class MediaControl
{
    /// <summary>Apps that "pause" paused, so "play" resumes those instead of whatever Windows picks.</summary>
    private static readonly HashSet<string> PausedByUs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pauses everything that's playing. Returns false if media sessions aren't available.</summary>
    public static async Task<bool> PauseAsync()
    {
        if (await GetSessionsAsync() is not { } sessions) return false;
        var playing = sessions.Where(IsPlaying).ToList();
        if (playing.Count == 0) return true; // nothing playing: do nothing (a toggle would start something)

        PausedByUs.Clear();
        foreach (var session in playing)
            if (await session.TryPauseAsync())
                PausedByUs.Add(session.SourceAppUserModelId);
        return true;
    }

    /// <summary>
    /// Resumes what "pause" paused; otherwise Windows' current media session.
    /// Returns false if media sessions aren't available.
    /// </summary>
    public static async Task<bool> PlayAsync()
    {
        if (await GetSessionsAsync() is not { } sessions) return false;
        if (sessions.Any(IsPlaying)) return true; // already playing: do nothing

        var resume = sessions.Where(s => PausedByUs.Contains(s.SourceAppUserModelId)).ToList();
        PausedByUs.Clear();
        if (resume.Count == 0 && Manager?.GetCurrentSession() is { } current) resume.Add(current);
        if (resume.Count == 0) return false; // no session to drive; caller falls back to the key

        foreach (var session in resume) await session.TryPlayAsync();
        return true;
    }

    private static GlobalSystemMediaTransportControlsSessionManager? Manager;

    private static async Task<IReadOnlyList<GlobalSystemMediaTransportControlsSession>?> GetSessionsAsync()
    {
        try
        {
            Manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return Manager.GetSessions();
        }
        catch
        {
            return null; // older Windows or the service is unavailable
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false; // session closed while we were looking
        }
    }
}
