using Jellyfin.Plugin.SeriesAudio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SeriesAudio.Tests;

public sealed class StartupCorrectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "series-audio-start-" + Guid.NewGuid());
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<IMediaSourceManager> _sources = new();
    private readonly StartupAudioCorrection _correction;
    private readonly Episode _episode = new() { Id = Guid.NewGuid(), SeasonId = Guid.NewGuid() };
    private readonly SessionInfo _session;

    public StartupCorrectionTests()
    {
        var store = new PreferenceStore(Path.Combine(_folder, "preferences.json"), NullLogger<PreferenceStore>.Instance);
        _session = new(_sessions.Object, NullLogger.Instance)
        {
            Id = "tv-session", UserId = Guid.NewGuid(), Client = "Jellyfin Android TV",
            Capabilities = new ClientCapabilities { SupportedCommands = [GeneralCommandType.SetAudioStreamIndex] },
            PlayState = new PlayerStateInfo { AudioStreamIndex = 1, MediaSourceId = "s" }
        };
        store.Set(_session.UserId, _episode.SeasonId, new("TVShows", "rus"));
        _sources.Setup(x => x.GetStaticMediaSources(_episode, false, null!)).Returns([
            new MediaSourceInfo { Id = "s", MediaStreams = [PreferenceTests.Audio(1, "HDRezka Studio"), PreferenceTests.Audio(5, "TVShows")] }
        ]);
        _sessions.Setup(x => x.SendGeneralCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _correction = new(_sessions.Object, _sources.Object, store, NullLogger<StartupAudioCorrection>.Instance);
    }

    private PlaybackStartEventArgs Start(string playId = "play") => new()
    { Item = _episode, Session = _session, MediaSourceId = "s", PlaySessionId = playId };
    private PlaybackProgressEventArgs Progress(bool automated = false) => new()
    { Item = _episode, Session = _session, MediaSourceId = "s", PlaySessionId = "play", IsAutomated = automated };

    [Fact]
    public async Task CorrectsOnceAndDoesNotFightManualSwitchOrTranscodeRestart()
    {
        await _correction.OnEvent(Start());
        await _correction.OnEvent(Progress());
        await _correction.OnEvent(Start("restart"));
        _sessions.Verify(x => x.SendGeneralCommand(null!, "tv-session",
            It.Is<GeneralCommand>(c => c.Name == GeneralCommandType.SetAudioStreamIndex && c.Arguments["Index"] == "5" && c.ControllingUserId == _session.UserId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AlreadyCorrectPlaybackLeavesLaterManualChoiceAlone()
    {
        _session.PlayState.AudioStreamIndex = 5;
        await _correction.OnEvent(Start());
        _session.PlayState.AudioStreamIndex = 1;
        await _correction.OnEvent(Progress());
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitsForRealProgressWhenStartHasNoAudioIndex()
    {
        _session.PlayState.AudioStreamIndex = null;
        await _correction.OnEvent(Start());
        _session.PlayState.AudioStreamIndex = 1;
        await _correction.OnEvent(Progress(automated: true));
        _sessions.VerifyNoOtherCalls();
        await _correction.OnEvent(Progress());
        _sessions.Verify(x => x.SendGeneralCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("Jellyfin Android TV", false)]
    public async Task DoesNotSendToOtherClientsOrClientsWithoutCapability(string client, bool supports)
    {
        _session.Client = client;
        _session.Capabilities = new ClientCapabilities { SupportedCommands = supports ? [GeneralCommandType.SetAudioStreamIndex] : [] };
        await _correction.OnEvent(Start());
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleProgressCannotConsumeTheNewPlaybackCorrection()
    {
        _session.PlayState.AudioStreamIndex = null;
        await _correction.OnEvent(Start());
        _session.PlayState.AudioStreamIndex = 1;
        var stale = Progress(); stale.PlaySessionId = "previous-playback";
        await _correction.OnEvent(stale);
        _sessions.VerifyNoOtherCalls();
        await _correction.OnEvent(Progress());
        _sessions.Verify(x => x.SendGeneralCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DoesNotGuessWrongSource()
    {
        var e = Start(); e.MediaSourceId = "unknown-version";
        await _correction.OnEvent(e);
        _sessions.VerifyNoOtherCalls();
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
