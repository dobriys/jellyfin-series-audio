using System.Collections.Concurrent;
using System.Globalization;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesAudio;

// Android TV 0.19.10 can prefer the last language over the server default.
// Correct startup once, never continuously enforce or learn from progress reports.
public sealed class StartupAudioCorrection(
    ISessionManager sessions,
    IMediaSourceManager sources,
    PreferenceStore preferences,
    ILogger<StartupAudioCorrection> logger)
    : IEventConsumer<PlaybackStartEventArgs>, IEventConsumer<PlaybackProgressEventArgs>
{
    private sealed record Pending(Guid ItemId, Guid UserId, string SourceId, string PlaySessionId, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _attempts = new();

    public async Task OnEvent(PlaybackStartEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pending.Where(p => p.Value.Expires < now)) _pending.TryRemove(pair.Key, out _);
        foreach (var pair in _attempts.Where(p => p.Value < now)) _attempts.TryRemove(pair.Key, out _);
        if (e.Session is null || e.Item is not Episode episode
            || episode.SeasonId == Guid.Empty
            || e.Session.Client?.Contains("Android TV", StringComparison.OrdinalIgnoreCase) != true
            || preferences.Get(e.Session.UserId, episode.SeasonId) is null) return;

        if (!e.Session.SupportedCommands.Contains(GeneralCommandType.SetAudioStreamIndex))
        {
            logger.LogDebug("Series Audio: client does not advertise audio switching; keeping server default only.");
            return;
        }
        _pending[e.Session.Id] = new(episode.Id, e.Session.UserId, e.MediaSourceId, e.PlaySessionId, now.AddSeconds(20));
        await Correct(e).ConfigureAwait(false);
    }

    public Task OnEvent(PlaybackProgressEventArgs e) => e.IsAutomated ? Task.CompletedTask : Correct(e);

    private async Task Correct(PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (session is null || !_pending.TryGetValue(session.Id, out var pending)) return;
        if (pending.Expires < DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(new KeyValuePair<string, Pending>(session.Id, pending));
            return;
        }
        // A late report from the previous episode must not consume the new episode's correction.
        if (e.Item?.Id != pending.ItemId || session.UserId != pending.UserId
            || e.PlaySessionId != pending.PlaySessionId || e.MediaSourceId != pending.SourceId) return;
        // Wait for a real index and a concrete source, rather than guessing source 0.
        if (session.PlayState.AudioStreamIndex is not int actual || string.IsNullOrWhiteSpace(pending.SourceId)) return;
        if (!_pending.TryRemove(new KeyValuePair<string, Pending>(session.Id, pending))) return;
        if (e.Item is not Episode episode) return;
        var preference = preferences.Get(session.UserId, episode.SeasonId);
        if (preference is null) return;
        var source = sources.GetStaticMediaSources(episode, false)
            .FirstOrDefault(s => s.Id.Equals(pending.SourceId, StringComparison.OrdinalIgnoreCase));
        var match = source is null ? null : AudioMatcher.Find(source.MediaStreams, preference);
        if (match is null || match.Index == actual) return;

        // Switching a transcode can restart playback. Suppress a restart loop.
        var attemptKey = $"{session.Id}/{episode.Id:N}/{pending.SourceId}";
        if (!_attempts.TryAdd(attemptKey, DateTimeOffset.UtcNow.AddSeconds(30))) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sessions.SendGeneralCommand(null!, session.Id, new GeneralCommand
            {
                Name = GeneralCommandType.SetAudioStreamIndex,
                ControllingUserId = session.UserId,
                Arguments = { ["Index"] = match.Index.ToString(CultureInfo.InvariantCulture) }
            }, timeout.Token).ConfigureAwait(false);
            logger.LogInformation("Series Audio: requested startup correction to audio index {Index}; client confirmation still required.", match.Index);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Series Audio: startup correction failed; no repeated switching will be attempted.");
        }
    }
}
