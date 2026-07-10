using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="LiveKitTokenService"/> — the LiveKit SDK containment point. Verifies
/// the configured/unconfigured gate and that a minted token carries the channel-scoped room grant
/// and the user identity, without any network dependency (a JWT is pure crypto over config).
/// </summary>
public class LiveKitTokenServiceTests
{
    private static LiveKitTokenService Build(
        string apiKey = "test-key",
        string apiSecret = "test-secret-at-least-32-characters-xx",
        string host = "wss://test.livekit.cloud"
    )
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LiveKit:ApiKey"] = apiKey,
                    ["LiveKit:ApiSecret"] = apiSecret,
                    ["LiveKit:Host"] = host,
                }
            )
            .Build();

        return new LiveKitTokenService(config, NullLogger<LiveKitTokenService>.Instance);
    }

    [Fact]
    public void IsConfigured_False_WhenKeysMissing()
    {
        var sut = Build(apiKey: "", apiSecret: "", host: "");

        sut.IsConfigured.Should().BeFalse();
        sut.CreateToken(123, 456, "alice", LiveKitTrackSources.All).Should().BeNull();
    }

    [Fact]
    public void IsConfigured_False_WhenHostMissing()
    {
        var sut = Build(host: "");

        sut.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void CreateToken_MintsJwt_WithRoomGrantAndIdentity()
    {
        var sut = Build();

        var token = sut.CreateToken(
            channelId: 999,
            userId: 42,
            displayName: "alice",
            LiveKitTrackSources.All
        );

        token.Should().NotBeNullOrEmpty();
        var payload = DecodeJwtPayload(token!);

        // Identity is the userId (single-session-per-user); name is the display name.
        payload.GetProperty("sub").GetString().Should().Be("42");
        payload.GetProperty("name").GetString().Should().Be("alice");

        // The video grant is scoped to the channel room and allows join + publish/subscribe.
        var video = payload.GetProperty("video");
        video.GetProperty("room").GetString().Should().Be("999");
        video.GetProperty("roomJoin").GetBoolean().Should().BeTrue();
        video.GetProperty("canPublish").GetBoolean().Should().BeTrue();
        video.GetProperty("canSubscribe").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CreateToken_IncludesCanPublishSources_InJwt()
    {
        var sut = Build();

        var token = sut.CreateToken(999, 42, "alice", LiveKitTrackSources.All);

        var video = DecodeJwtPayload(token!).GetProperty("video");
        video
            .GetProperty("canPublishSources")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo("microphone", "camera", "screen_share", "screen_share_audio");
    }

    [Fact]
    public void CreateToken_MicOnly_OmitsCameraAndScreen()
    {
        var sut = Build();

        var token = sut.CreateToken(999, 42, "alice", [LiveKitTrackSources.Microphone]);

        var video = DecodeJwtPayload(token!).GetProperty("video");
        video
            .GetProperty("canPublishSources")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo("microphone");
    }

    [Fact]
    public void Url_ReflectsConfiguredHost()
    {
        Build(host: "wss://my-project.livekit.cloud").Url.Should().Be("wss://my-project.livekit.cloud");
    }

    /// <summary>Base64url-decodes the middle segment of a JWT into its JSON claims.</summary>
    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
