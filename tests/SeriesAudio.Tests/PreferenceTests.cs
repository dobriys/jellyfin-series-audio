using Jellyfin.Database.Implementations.Entities;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Jellyfin.Plugin.SeriesAudio;

namespace SeriesAudio.Tests;

public sealed class PreferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "series-audio-test-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "preferences.json");
    private PreferenceStore Store() => new(FilePath, NullLogger<PreferenceStore>.Instance);
    internal static MediaStream Audio(int index, string? title, string language = "rus") => new()
    { Index = index, Title = title, Language = language, Type = MediaStreamType.Audio };

    [Fact]
    public void FindsStudioAfterReorderingWithoutConfusingAdultVersion()
    {
        var preference = new AudioPreference("HDRezka Studio", "ru");
        Assert.Equal(1, AudioMatcher.Find([Audio(1, "HDRezka Studio"), Audio(2, "HDRezka Studio 18+")], preference)?.Index);
        Assert.Equal(8, AudioMatcher.Find([Audio(1, "HDRezka Studio 18+"), Audio(8, "  HDREZKA   Studio ")], preference)?.Index);
        Assert.Null(AudioMatcher.Find([Audio(1, "HDRezka Studio 18+")], preference));
        Assert.Null(AudioMatcher.Find([Audio(8, "HDRezka Studio", "eng")], preference));
    }

    [Fact]
    public void UnnamedTracksRequireUnambiguousLanguage()
    {
        Assert.Equal(0, AudioMatcher.Find([Audio(0, null, "en")], new("", "eng"))?.Index);
        Assert.Null(AudioMatcher.Find([Audio(0, null), Audio(3, "")], new("", "rus")));
        Assert.Null(AudioMatcher.Find([Audio(0, "", "")], new("", "")));
        Assert.Equal(new AudioPreference("", "eng"), AudioMatcher.FromStream(Audio(0, " Unknown ", "en")));
        Assert.Equal(0, AudioMatcher.Find([Audio(0, "Unknown", "eng")], new("", "eng"))?.Index);
        Assert.Null(AudioMatcher.Find([Audio(0, "Unknown", "und")], new("", "und")));
        Assert.Null(AudioMatcher.Find([new MediaStream { Index = 2, Title = "Studio", Language = "rus", Type = MediaStreamType.Subtitle }], new("Studio", "rus")));
    }

    [Fact]
    public void PreferencesSurviveRestartAndResetWithoutAffectingOthers()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var season = Guid.NewGuid(); var otherSeason = Guid.NewGuid();
        var store = Store();
        store.Set(first, season, new("AlexFilm", "rus"));
        store.Set(second, season, new("TVShows", "rus"));
        store.Set(first, otherSeason, new("NewComers", "rus"));
        var reloaded = Store();
        Assert.Equal("AlexFilm", reloaded.Get(first, season)?.Title);
        reloaded.Set(first, season, null);
        Assert.Null(Store().Get(first, season));
        Assert.Equal("TVShows", Store().Get(second, season)?.Title);
        Assert.Equal("NewComers", Store().Get(first, otherSeason)?.Title);
    }

    [Fact]
    public void KeepsLegacySeriesPreferencesForRollback()
    {
        Directory.CreateDirectory(_directory);
        var userId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var legacyKey = $"{userId:N}/{seasonId:N}";
        var legacy = new AudioPreference("Legacy Studio", "rus");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dictionary<string, AudioPreference> { [legacyKey] = legacy }));

        var store = Store();
        Assert.Null(store.Get(userId, seasonId));
        store.Set(userId, seasonId, new("Season Studio", "rus"));
        store.Set(userId, seasonId, null);

        var remaining = JsonSerializer.Deserialize<Dictionary<string, AudioPreference>>(File.ReadAllText(FilePath));
        Assert.Equal(legacy, Assert.Single(remaining!).Value);
        Assert.Equal(legacy, remaining![legacyKey]);
    }

    [Fact]
    public void ConcurrentSavesDoNotLoseUsers()
    {
        var store = Store(); var season = Guid.NewGuid(); var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();
        Parallel.ForEach(ids, id => store.Set(id, season, new("TVShows", "rus")));
        var reloaded = Store();
        Assert.All(ids, id => Assert.Equal("TVShows", reloaded.Get(id, season)?.Title));
    }

    [Fact]
    public void CorruptStoreIsNotOverwritten()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(FilePath, "broken");
        var store = Store();
        Assert.Null(store.Get(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => store.Set(Guid.NewGuid(), Guid.NewGuid(), new("Studio", "rus")));
        Assert.Equal("broken", File.ReadAllText(FilePath));
    }

    [Fact]
    public void SourceSelectionIsPerUserAndLeavesOriginalAndMissingSourcesIntact()
    {
        var store = Store(); var user = new User("test", "auth", "reset") { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid(), SeasonId = Guid.NewGuid() };
        var source = new MediaSourceInfo { Id = "source", DefaultAudioStreamIndex = 1, DefaultAudioIndexSource = AudioIndexSource.Default,
            MediaStreams = [Audio(1, "Default"), Audio(6, "Dub | Red Head Sound")] };
        var missing = new MediaSourceInfo { Id = "other-version", DefaultAudioStreamIndex = 1, MediaStreams = [Audio(1, "Default")] };
        store.Set(user.Id, episode.SeasonId, new("Dub | Red Head Sound", "rus"));
        var manager = new PreferredMediaSources(Mock.Of<IMediaSourceManager>(), store);
        var selected = manager.Apply(episode, user, [source, missing]);
        Assert.Equal(6, selected[0].DefaultAudioStreamIndex);
        Assert.Equal(AudioIndexSource.User, selected[0].DefaultAudioIndexSource);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Same(missing, selected[1]);
        Assert.Same(source, manager.Apply(episode, null, [source])[0]);
        var otherSeason = new Episode { SeasonId = Guid.NewGuid(), SeriesId = episode.SeriesId };
        Assert.Same(source, manager.Apply(otherSeason, user, [source])[0]);
        Assert.Same(source, manager.Apply(new Movie(), user, [source])[0]);
        var other = new User("other", "auth", "reset") { Id = Guid.NewGuid() };
        Assert.Same(source, manager.Apply(episode, other, [source])[0]);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
