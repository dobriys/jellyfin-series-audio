using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SeriesAudio;

[ApiController]
[Authorize]
[Route("SeriesAudio")]
public sealed class SeriesAudioController(
    IUserManager users, ILibraryManager library, IMediaSourceManager sources, PreferenceStore preferences) : ControllerBase
{
    public sealed record SelectionRequest(
        [property: JsonPropertyName("episodeId")] Guid EpisodeId,
        [Required, MaxLength(200)] [property: JsonPropertyName("mediaSourceId")] string MediaSourceId,
        [Range(0, int.MaxValue)] [property: JsonPropertyName("audioStreamIndex")] int AudioStreamIndex);

    public sealed record Choice(
        [property: JsonPropertyName("preference")] AudioPreference Preference,
        [property: JsonPropertyName("episodeId")] Guid EpisodeId,
        [property: JsonPropertyName("mediaSourceId")] string MediaSourceId,
        [property: JsonPropertyName("audioStreamIndex")] int AudioStreamIndex);

    public sealed record ChoicesResponse(
        [property: JsonPropertyName("preference")] AudioPreference? Preference,
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice> Choices,
        [property: JsonPropertyName("defaults")] IReadOnlyList<AudioPreference?> Defaults);

    public sealed record SeasonChoices(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("audio")] ChoicesResponse Audio);

    // Use a real authenticated user; a caller cannot impersonate another profile.
    private User? ResolveUser(Guid? targetUserId)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var callerId)) return null;
        var target = targetUserId ?? callerId;
        if (target != callerId && !User.IsInRole("Administrator")) return null;
        return users.GetUserById(target);
    }

    [HttpGet("Season/{seasonId:guid}")]
    public ActionResult<ChoicesResponse> GetChoices(Guid seasonId, [FromQuery] Guid? userId)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        return library.GetItemById<Season>(seasonId, user) is { } season
            ? BuildChoices(season, user)
            : NotFound();
    }

    [HttpGet("Series/{seriesId:guid}/Seasons")]
    public ActionResult<SeasonChoices[]> GetSeriesChoices(Guid seriesId, CancellationToken cancellationToken)
    {
        var user = ResolveUser(null);
        if (user is null) return Forbid();
        if (library.GetItemById<Series>(seriesId, user) is not { } series) return NotFound();

        var results = new List<SeasonChoices>();
        foreach (var season in series.GetSeasons(user, new DtoOptions()).OfType<Season>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (season.SeriesId != seriesId || library.GetItemById<Season>(season.Id, user) is null) continue;
            var audio = BuildChoices(season, user);
            if (audio.Choices.Count >= 2 || audio.Defaults.Any(track => track is not null))
                results.Add(new(season.Id, season.Name, audio));
        }
        return results.ToArray();
    }

    private ChoicesResponse BuildChoices(Season season, User user)
    {
        var episodes = season.GetEpisodes(user, new DtoOptions(), shouldIncludeMissingEpisodes: false)
            .OfType<Episode>().Where(episode => episode.SeasonId == season.Id);
        var choices = new Dictionary<string, Choice>();
        var defaults = new Dictionary<string, AudioPreference?>();
        foreach (var episode in episodes)
        {
            var media = sources.GetStaticMediaSources(episode, false, user);
            var track = GetDefaultTrack(media.FirstOrDefault());
            defaults.TryAdd(track is null ? "" : AudioMatcher.Key(track), track);

            var episodeChoices = media.SelectMany(source => source.MediaStreams.Where(stream => stream.Type == MediaStreamType.Audio)
                .Select(AudioMatcher.FromStream)
                .DistinctBy(AudioMatcher.Key)
                .Select(preference => (Preference: preference, Stream: AudioMatcher.Find(source.MediaStreams, preference)))
                .Where(match => match.Stream is not null)
                .Select(match => new Choice(match.Preference, episode.Id, source.Id, match.Stream!.Index)));
            foreach (var choice in episodeChoices) choices.TryAdd(AudioMatcher.Key(choice.Preference), choice);
        }

        return new ChoicesResponse(
            preferences.Get(user.Id, season.Id),
            choices.Values.OrderBy(choice => choice.Preference.Language).ThenBy(choice => choice.Preference.Title).ToArray(),
            defaults.Values.ToArray());
    }

    private static AudioPreference? GetDefaultTrack(MediaSourceInfo? source)
    {
        if (source is null || source.GetDefaultAudioStream(source.DefaultAudioStreamIndex) is not { } stream) return null;
        var track = AudioMatcher.FromStream(stream);
        if (track.Title.Length > 0 || AudioMatcher.Find(source.MediaStreams, track) is not null) return track;

        var number = source.MediaStreams.TakeWhile(candidate => candidate != stream)
            .Count(candidate => candidate.Type == MediaStreamType.Audio) + 1;
        return track with { Title = $"Дорожка {number}" };
    }

    [HttpPut("Season/{seasonId:guid}")]
    public IActionResult SetPreference(Guid seasonId, [FromQuery] Guid? userId, [FromBody] SelectionRequest selection)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        if (library.GetItemById<Season>(seasonId, user) is null) return NotFound();
        var episode = library.GetItemById<Episode>(selection.EpisodeId, user);
        if (episode is null || episode.SeasonId != seasonId) return BadRequest("Эпизод не принадлежит этому сезону или недоступен.");
        var source = sources.GetStaticMediaSources(episode, false, user)
            .FirstOrDefault(s => s.Id.Equals(selection.MediaSourceId, StringComparison.OrdinalIgnoreCase));
        var stream = source?.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == selection.AudioStreamIndex);
        if (stream is null) return BadRequest("Аудиодорожка не найдена. Обновите страницу.");
        var preference = AudioMatcher.FromStream(stream);
        if (AudioMatcher.Find(source!.MediaStreams, preference) is null)
            return BadRequest("Дорожка без названия неоднозначна. Сначала задайте ей уникальное название.");
        preferences.Set(user.Id, seasonId, preference);
        return NoContent();
    }

    [HttpDelete("Season/{seasonId:guid}")]
    public IActionResult ResetPreference(Guid seasonId, [FromQuery] Guid? userId)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        if (library.GetItemById<Season>(seasonId, user) is null) return NotFound();
        preferences.Set(user.Id, seasonId, null);
        return NoContent();
    }

    // Public code only: all preference and library endpoints above require authentication.
    [AllowAnonymous]
    [HttpGet("web.js")]
    [ResponseCache(NoStore = true)]
    public IActionResult GetWebScript()
    {
        var script = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.SeriesAudio.Web.series-audio.js")!;
        return File(script, "application/javascript; charset=utf-8");
    }
}
