using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.SeriesAudio;

// Decorate the official source service BEFORE StreamBuilder negotiates playback.
// Changing only the final response would leave a transcoding URL with the wrong audio.
public sealed class PreferredMediaSources(IMediaSourceManager inner, PreferenceStore preferences) : IMediaSourceManager
{
    public IReadOnlyList<MediaSourceInfo> Apply(BaseItem item, User? user, IReadOnlyList<MediaSourceInfo> sources)
    {
        if (user is null || item is not Episode episode) return sources;
        var preference = preferences.Get(user.Id, episode.SeriesId);
        if (preference is null) return sources;

        return sources.Select(source =>
        {
            var match = AudioMatcher.Find(source.MediaStreams, preference);
            if (match is null) return source;
            // Never mutate sources shared with another user/request.
            var copy = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(source))!;
            copy.DefaultAudioStreamIndex = match.Index;
            copy.DefaultAudioIndexSource = AudioIndexSource.User;
            return copy;
        }).ToArray();
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken) =>
        Apply(item, user, await inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false));

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null!) =>
        Apply(item, user, inner.GetStaticMediaSources(item, enablePathSubstitution, user));

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
    {
        inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
        var preferred = Apply(item, user, new[] { source })[0];
        source.DefaultAudioStreamIndex = preferred.DefaultAudioStreamIndex;
        source.DefaultAudioIndexSource = preferred.DefaultAudioIndexSource;
    }

    // Keep live TV, probing and other media behavior delegated to Jellyfin.
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => inner.GetMediaStreams(itemId);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => inner.GetMediaAttachments(itemId);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => inner.OpenLiveStream(request, cancellationToken);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => inner.OpenLiveStreamInternal(request, cancellationToken);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => inner.GetLiveStream(id, cancellationToken);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);
    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => inner.GetLiveStreamInfoByUniqueId(uniqueId);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => inner.GetRecordingStreamMediaSources(info, cancellationToken);
    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => inner.GetLiveStreamMediaInfo(id, cancellationToken);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);
    public Task AddMediaInfoWithProbe(MediaSourceInfo source, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => inner.AddMediaInfoWithProbe(source, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
}
