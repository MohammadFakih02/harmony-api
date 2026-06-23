using FluentAssertions;
using Harmony.Application.Services;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Pure tokenizer tests — no mocks. Covers exact-match semantics, case-insensitivity,
/// the @everyone/@here guild-only gate, and token-boundary behavior.
/// </summary>
public class MentionParserTests
{
    private static readonly Dictionary<string, long> Candidates = new()
    {
        ["alice"] = 1,
        ["bob"] = 2,
        ["charlie.dev"] = 3,
    };

    [Fact]
    public void Parse_ShouldResolveExactUsernameMatch()
    {
        var result = MentionParser.Parse("hey @alice how's it going", Candidates, guildContext: true);

        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 1 });
        result.Everyone.Should().BeFalse();
        result.Here.Should().BeFalse();
    }

    [Fact]
    public void Parse_ShouldBeCaseInsensitive()
    {
        var result = MentionParser.Parse("@ALICE @Bob", Candidates, guildContext: true);

        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 1, 2 });
    }

    [Fact]
    public void Parse_ShouldIgnoreUnknownUsername()
    {
        var result = MentionParser.Parse("@nonexistent says hi", Candidates, guildContext: true);

        result.UserIds.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldDeduplicateRepeatedMentions()
    {
        var result = MentionParser.Parse("@alice @alice @alice", Candidates, guildContext: true);

        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 1 });
    }

    [Fact]
    public void Parse_ShouldCaptureMaximalRun_AndNotMatchPartialUsername()
    {
        // "@aliceandbob" is a single, longer token — must NOT fuzzy/prefix-match "alice".
        var result = MentionParser.Parse("@aliceandbob", Candidates, guildContext: true);

        result.UserIds.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldMatchUsernameContainingDots()
    {
        var result = MentionParser.Parse("ping @charlie.dev", Candidates, guildContext: true);

        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 3 });
    }

    [Theory]
    [InlineData("@everyone")]
    [InlineData("@EVERYONE")]
    public void Parse_ShouldDetectEveryone_InGuildContext(string content)
    {
        var result = MentionParser.Parse(content, Candidates, guildContext: true);

        result.Everyone.Should().BeTrue();
        result.UserIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("@here")]
    [InlineData("@HERE")]
    public void Parse_ShouldDetectHere_InGuildContext(string content)
    {
        var result = MentionParser.Parse(content, Candidates, guildContext: true);

        result.Here.Should().BeTrue();
    }

    [Fact]
    public void Parse_ShouldNotTreatEveryoneOrHereAsLiteral_OutsideGuildContext()
    {
        var result = MentionParser.Parse("@everyone @here @alice", Candidates, guildContext: false);

        result.Everyone.Should().BeFalse();
        result.Here.Should().BeFalse();
        // "everyone"/"here" are simply not in the candidate dictionary, so they resolve to nothing —
        // only @alice (a real DM participant) matches.
        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 1 });
    }

    [Fact]
    public void Parse_ShouldTerminateTokenAtPunctuation()
    {
        var result = MentionParser.Parse("@alice, hi!", Candidates, guildContext: true);

        result.UserIds.Should().BeEquivalentTo(new HashSet<long> { 1 });
    }

    [Fact]
    public void Parse_ShouldReturnEmpty_WhenNoMentionsPresent()
    {
        var result = MentionParser.Parse("just a normal message, no mentions here", Candidates, guildContext: true);

        result.UserIds.Should().BeEmpty();
        result.Everyone.Should().BeFalse();
        result.Here.Should().BeFalse();
    }

    [Fact]
    public void Parse_ShouldIgnoreBareAtSign()
    {
        var result = MentionParser.Parse("email me @ noon", Candidates, guildContext: true);

        result.UserIds.Should().BeEmpty();
    }
}
