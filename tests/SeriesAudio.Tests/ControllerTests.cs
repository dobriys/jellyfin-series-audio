using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeriesAudio;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SeriesAudio.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "series-audio-api-" + Guid.NewGuid());
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IMediaSourceManager> _sources = new();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();
    private readonly User _caller = new("caller", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _other = new("other", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly PreferenceStore _store;

    public ControllerTests()
    {
        _store = new(Path.Combine(_folder, "preferences.json"), NullLogger<PreferenceStore>.Instance);
        foreach (var user in new[] { _caller, _other })
        {
            _users.Setup(x => x.GetUserById(user.Id)).Returns(user);
            _library.Setup(x => x.GetItemById<Season>(_season, user)).Returns(new Season { Id = _season, SeriesId = SeriesId });
        }
    }

    private SeriesAudioController Controller(bool admin = false, bool authenticatedUser = true)
    {
        var claims = new List<Claim>();
        if (authenticatedUser) claims.Add(new("Jellyfin-UserId", _caller.Id.ToString()));
        if (admin) claims.Add(new(ClaimTypes.Role, "Administrator"));
        return new(_users.Object, _library.Object, _sources.Object, _store)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "test")) } } };
    }

    [Fact]
    public void UserCannotReadWriteOrResetAnotherProfile()
    {
        var controller = Controller();
        Assert.IsType<ForbidResult>(controller.GetChoices(_season, _other.Id).Result);
        Assert.IsType<ForbidResult>(controller.SetPreference(_season, _other.Id, new(Guid.NewGuid(), "s", 1)));
        Assert.IsType<ForbidResult>(controller.ResetPreference(_season, _other.Id));
        Assert.IsType<ForbidResult>(Controller(authenticatedUser: false).ResetPreference(_season, null));
        _sources.VerifyNoOtherCalls();
    }

    [Fact]
    public void AdminCanSetAnotherProfileUsingServerVerifiedTrack()
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, SeasonId = _season };
        _library.Setup(x => x.GetItemById<Episode>(episode.Id, _other)).Returns(episode);
        _sources.Setup(x => x.GetStaticMediaSources(episode, false, _other)).Returns([
            new MediaSourceInfo { Id = "s", MediaStreams = [PreferenceTests.Audio(6, "Dub | Red Head Sound")] }
        ]);
        var result = Controller(admin: true).SetPreference(_season, _other.Id, new(episode.Id, "s", 6));
        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Dub | Red Head Sound", _store.Get(_other.Id, _season)?.Title);
        Assert.Null(_store.Get(_caller.Id, _season));
        Assert.IsType<NoContentResult>(Controller(admin: true).ResetPreference(_season, _other.Id));
        Assert.Null(_store.Get(_other.Id, _season));
    }

    [Fact]
    public void RejectsEpisodeFromAnotherSeasonAndInaccessibleSeries()
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, SeasonId = Guid.NewGuid() };
        _library.Setup(x => x.GetItemById<Episode>(episode.Id, _caller)).Returns(episode);
        Assert.IsType<BadRequestObjectResult>(Controller().SetPreference(_season, null, new(episode.Id, "s", 1)));
        Assert.IsType<NotFoundResult>(Controller().SetPreference(Guid.NewGuid(), null, new(episode.Id, "s", 1)));
        Assert.Null(_store.Get(_caller.Id, _season));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
