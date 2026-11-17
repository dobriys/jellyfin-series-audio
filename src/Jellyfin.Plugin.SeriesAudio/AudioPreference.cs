using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SeriesAudio;

public sealed record AudioPreference(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("language")] string Language);

public static partial class AudioMatcher
{
    private static string NormalizeTitle(string? value) => Whitespace().Replace(
        (value ?? "").Normalize(NormalizationForm.FormKC).Trim(), " ").ToLowerInvariant() switch
        {
            "unknown" or "und" or "неизвестно" or "без названия" => "",
            var title => title
        };

    public static string NormalizeLanguage(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "ru" or "rus" => "rus",
        "en" or "eng" => "eng",
        "uk" or "ukr" => "ukr",
        "he" or "heb" => "heb",
        "und" or "unknown" or "zxx" => "",
        var language => language
    };

    public static AudioPreference FromStream(MediaStream stream) =>
        new(NormalizeTitle(stream.Title).Length == 0 ? "" : stream.Title.Trim(), NormalizeLanguage(stream.Language));

    public static string Key(AudioPreference preference) =>
        NormalizeLanguage(preference.Language) + "\n" + NormalizeTitle(preference.Title);

    public static MediaStream? Find(IEnumerable<MediaStream> streams, AudioPreference preference)
    {
        var title = NormalizeTitle(preference.Title);
        var language = NormalizeLanguage(preference.Language);
        if (title.Length == 0 && language.Length == 0) return null;

        var candidates = streams.Where(s => s.Type == MediaStreamType.Audio
            && NormalizeLanguage(s.Language) == language
            && NormalizeTitle(s.Title) == title).ToArray();

        // An unnamed track is identifiable only when there is a single candidate.
        if (title.Length == 0 && candidates.Length != 1) return null;
        return candidates.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Index).FirstOrDefault();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
