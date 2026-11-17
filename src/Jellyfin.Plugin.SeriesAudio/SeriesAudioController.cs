using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
        [property: JsonPropertyName("sampledEpisodes")] int SampledEpisodes,
        [property: JsonPropertyName("hasMoreEpisodes")] bool HasMoreEpisodes);

    // Use a real authenticated user; a caller cannot impersonate another profile.
    private User? ResolveUser(Guid? targetUserId)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var callerId)) return null;
        var target = targetUserId ?? callerId;
        if (target != callerId && !User.IsInRole("Administrator")) return null;
        return users.GetUserById(target);
    }

    [HttpGet("Series/{seriesId:guid}")]
    public ActionResult<ChoicesResponse> GetChoices(Guid seriesId, [FromQuery] Guid? userId, [FromQuery] Guid? episodeId)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        if (library.GetItemById<Series>(seriesId, user) is null) return NotFound();
        var found = library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = seriesId, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode], Limit = 101
        });
        var episodes = found.OfType<Episode>().Take(100).ToList();
        // Include the current episode even if it falls outside the first 100.
        if (episodeId.HasValue && library.GetItemById<Episode>(episodeId.Value, user) is { } current
            && current.SeriesId == seriesId && episodes.All(e => e.Id != current.Id)) episodes.Insert(0, current);
        var choices = episodes.SelectMany(episode => sources.GetStaticMediaSources(episode, false, user)
            .SelectMany(source => source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio)
                .Where(s => !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Language))
                .Select(s => new Choice(AudioMatcher.FromStream(s), episode.Id, source.Id, s.Index))))
            .DistinctBy(c => AudioMatcher.Key(c.Preference))
            .OrderBy(c => c.Preference.Language).ThenBy(c => c.Preference.Title).ToArray();
        return new ChoicesResponse(preferences.Get(user.Id, seriesId), choices, episodes.Count, found.Count > 100);
    }

    [HttpPut("Series/{seriesId:guid}")]
    public IActionResult SetPreference(Guid seriesId, [FromQuery] Guid? userId, [FromBody] SelectionRequest selection)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        if (library.GetItemById<Series>(seriesId, user) is null) return NotFound();
        var episode = library.GetItemById<Episode>(selection.EpisodeId, user);
        if (episode is null || episode.SeriesId != seriesId) return BadRequest("Эпизод не принадлежит этому сериалу или недоступен.");
        var source = sources.GetStaticMediaSources(episode, false, user)
            .FirstOrDefault(s => s.Id.Equals(selection.MediaSourceId, StringComparison.OrdinalIgnoreCase));
        var stream = source?.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == selection.AudioStreamIndex);
        if (stream is null) return BadRequest("Аудиодорожка не найдена. Обновите страницу.");
        var preference = AudioMatcher.FromStream(stream);
        if (AudioMatcher.Find(source!.MediaStreams, preference) is null)
            return BadRequest("Дорожка без названия неоднозначна. Сначала задайте ей уникальное название.");
        preferences.Set(user.Id, seriesId, preference);
        return NoContent();
    }

    [HttpDelete("Series/{seriesId:guid}")]
    public IActionResult ResetPreference(Guid seriesId, [FromQuery] Guid? userId)
    {
        var user = ResolveUser(userId);
        if (user is null) return Forbid();
        if (library.GetItemById<Series>(seriesId, user) is null) return NotFound();
        preferences.Set(user.Id, seriesId, null);
        return NoContent();
    }

    // Public code only: all preference and library endpoints above require authentication.
    [AllowAnonymous]
    [HttpGet("web.js")]
    public IActionResult GetWebScript()
    {
        var script = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.SeriesAudio.Web.series-audio.js")!;
        return File(script, "application/javascript; charset=utf-8");
    }
}
