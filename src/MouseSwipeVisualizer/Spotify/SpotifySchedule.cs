namespace MouseSwipeVisualizer.Spotify;

/// <summary>Which song's end the poller is waiting for, and how many quick re-checks were used.</summary>
public struct SongEndWatch
{
    public string? TrackId;
    public int Retries;
}

/// <summary>
/// When to ask Spotify again. Smart timing: while a song plays, check right after it should end
/// (duration − progress + a small margin), then re-check a few times shortly after in case Spotify still
/// reports the old song. The regular interval stays as a slower safety check, because skips, pauses and
/// song picks are not announced by the Web API and are only seen at the next check.
/// </summary>
public static class SpotifySchedule
{
    /// <summary>Checked this long after the song should have ended.</summary>
    public static readonly TimeSpan EndMargin = TimeSpan.FromMilliseconds(800);

    /// <summary>Re-checks when the old song is still reported at (or just past) its end.</summary>
    public static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };

    /// <summary>"At its end": less than this much of the song left.</summary>
    public static readonly TimeSpan NearEnd = TimeSpan.FromSeconds(2);

    public static TimeSpan NextDelay(NowPlaying? now, TimeSpan safetyInterval, bool smart, ref SongEndWatch watch)
    {
        if (!smart || now is not { IsPlaying: true } || now.DurationMs <= 0)
        {
            watch = default;
            return safetyInterval;
        }

        var remaining = TimeSpan.FromMilliseconds(Math.Max(0, now.DurationMs - now.ProgressMs));
        string id = now.Id ?? now.Title + "\u001f" + now.Artist;
        if (watch.TrackId == id && remaining < NearEnd)
        {
            // The song should be over but Spotify still reports it: re-check quickly a few times.
            if (watch.Retries < RetryDelays.Length)
            {
                return Min(RetryDelays[watch.Retries++], safetyInterval);
            }

            watch = default; // e.g. stuck at the end or buffering: back to the regular interval
            return safetyInterval;
        }

        watch = new SongEndWatch { TrackId = id, Retries = 0 };
        return Min(remaining + EndMargin, safetyInterval);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
