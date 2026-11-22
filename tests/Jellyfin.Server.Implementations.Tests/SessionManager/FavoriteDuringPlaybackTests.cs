using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

/// <summary>
/// A session keeps the item instance it started playing, while the favorite endpoint loads a new
/// instance of the same song. Playback reports must not write the session's older copy of the
/// user data over the favorite.
/// </summary>
public sealed class FavoriteDuringPlaybackTests : IAsyncDisposable
{
    private static readonly long _songRunTimeTicks = TimeSpan.FromMinutes(4).Ticks;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly UserDataManager _userDataManager;
    private readonly Emby.Server.Implementations.Session.SessionManager _sessionManager;
    private readonly User _user;
    private readonly Guid _songId = Guid.NewGuid();

    public FavoriteDuringPlaybackTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        _user = new User("user", "auth-provider", "reset-provider")
        {
            Id = Guid.NewGuid()
        };

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
            ctx.Users.Add(_user);
            ctx.BaseItems.Add(new BaseItemEntity { Id = _songId, Type = typeof(Audio).FullName! });
            ctx.SaveChanges();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);

        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        _userDataManager = new UserDataManager(config.Object, factory.Object);

        _sessionManager = new Emby.Server.Implementations.Session.SessionManager(
            NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
            Mock.Of<IEventManager>(),
            _userDataManager,
            config.Object,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IMusicManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<IDeviceManager>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IHostApplicationLifetime>());
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionManager.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void FavoriteDuringPlayback_SurvivesProgressReport()
    {
        var playing = CreateSong();
        Invoke("OnPlaybackStart", _user, playing);

        SetFavorite(true);
        Invoke("OnPlaybackProgress", _user, playing, new PlaybackProgressInfo { ItemId = _songId, PositionTicks = TimeSpan.FromSeconds(30).Ticks });

        AssertStoredFavorite(true);
    }

    [Fact]
    public void FavoriteDuringPlayback_SurvivesStopReport()
    {
        var playing = CreateSong();
        Invoke("OnPlaybackStart", _user, playing);

        SetFavorite(true);
        Invoke("OnPlaybackStopped", _user, playing, (long?)TimeSpan.FromSeconds(30).Ticks, false);

        AssertStoredFavorite(true);
    }

    [Fact]
    public void UnfavoriteDuringPlayback_SurvivesProgressReport()
    {
        SetFavorite(true);

        var playing = CreateSong();
        Invoke("OnPlaybackStart", _user, playing);

        SetFavorite(false);
        Invoke("OnPlaybackProgress", _user, playing, new PlaybackProgressInfo { ItemId = _songId, PositionTicks = TimeSpan.FromSeconds(30).Ticks });

        AssertStoredFavorite(false);
    }

    [Fact]
    public void StopReport_StillSavesPlaybackState()
    {
        var playing = CreateSong();
        Invoke("OnPlaybackStart", _user, playing);

        SetFavorite(true);
        Invoke("OnPlaybackStopped", _user, playing, (long?)_songRunTimeTicks, false);

        using var ctx = CreateDbContext();
        var rows = ctx.UserData.Where(e => e.ItemId.Equals(_songId)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.True(row.IsFavorite);
            Assert.True(row.Played);
            Assert.Equal(1, row.PlayCount);
        });
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    // Songs aren't kept in the library item cache, so every lookup of the same id gives a new instance.
    private Audio CreateSong()
    {
        var song = new Audio
        {
            Id = _songId,
            Name = "Song",
            Album = "Album",
            AlbumArtists = ["Artist"],
            IndexNumber = 1,
            RunTimeTicks = _songRunTimeTicks
        };

        using var ctx = CreateDbContext();
        song.UserData = ctx.UserData.AsNoTracking().Where(e => e.ItemId.Equals(_songId)).ToArray();
        return song;
    }

    // What the favorite endpoint does: load the song again and save the change through that instance.
    private void SetFavorite(bool isFavorite)
    {
        _userDataManager.SaveUserData(
            _user,
            CreateSong(),
            new UpdateUserItemDataDto { IsFavorite = isFavorite },
            UserDataSaveReason.UpdateUserRating);
    }

    private void AssertStoredFavorite(bool expected)
    {
        using var ctx = CreateDbContext();
        var rows = ctx.UserData.Where(e => e.ItemId.Equals(_songId) && e.UserId.Equals(_user.Id)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(expected, row.IsFavorite));
    }

    private void Invoke(string name, params object?[] args)
    {
        var parameterTypes = Array.ConvertAll(args, a => a!.GetType());
        parameterTypes[1] = typeof(BaseItem);
        if (args.Length == 4)
        {
            parameterTypes[2] = typeof(long?);
        }

        typeof(Emby.Server.Implementations.Session.SessionManager)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, parameterTypes)!
            .Invoke(_sessionManager, args);
    }
}
