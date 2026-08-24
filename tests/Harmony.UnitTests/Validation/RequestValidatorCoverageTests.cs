using System.Reflection;
using FluentAssertions;
using FluentValidation;
using Harmony.Application.DTOs.Requests;
using Xunit;

namespace Harmony.UnitTests.Validation;

/// <summary>
/// Closes the fail-open gap in <c>ValidationActionFilter</c> (audit A4): an action argument with no
/// registered <see cref="IValidator{T}"/> passes through <em>untouched</em>. That is the right runtime
/// behaviour (not every body needs shape rules), but it means a new request DTO can silently ship with
/// zero validation and nobody notices. This test makes coverage explicit and enforced: every request
/// record in <c>DTOs/Requests</c> must EITHER have a validator OR appear on the allow-list below with a
/// documented reason. Adding a request type without doing one of those two things fails the build.
/// </summary>
public class RequestValidatorCoverageTests
{
    private static readonly Assembly ApplicationAssembly = typeof(RegisterRequest).Assembly;
    private const string RequestsNamespace = "Harmony.Application.DTOs.Requests";

    /// <summary>
    /// Request DTOs that deliberately have no FluentValidation validator, each with the reason. Keep
    /// this list honest — <see cref="AllowList_HasNoStaleOrRedundantEntries"/> fails if an entry stops
    /// being a request type or gains a validator (meaning it should be removed from here).
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> NoValidatorByDesign = new Dictionary<Type, string>
    {
        // Free-form content is fully validated in-service, where the single source of truth lives:
        [typeof(ReactionRequest)] = "Emoji validated by MessageService.ValidateEmoji (grapheme/whitespace/reserved-prefix rules can't be a simple shape rule).",

        // Enum-token strings validated against their canonical set at the controller:
        [typeof(SetNotificationLevelRequest)] = "Level validated via NotificationLevel.IsValid in GuildNotificationSettingsController.",
        [typeof(UpdateDmPrivacyRequest)] = "Audiences validated via DmPrivacy.AllowedTokens in UsersController.",

        // All-nullable partial-update flags — nothing to shape-check:
        [typeof(UpdateNotificationPreferenceRequest)] = "Every field is a nullable bool flag; no shape rule applies.",
        [typeof(SetSuppressEveryoneRequest)] = "Single bool; no shape rule applies.",

        // A single scalar id/flag — identity/ownership is semantic, resolved server-side:
        [typeof(MarkReadRequest)] = "Single message id; no shape rule (ownership is semantic).",
        [typeof(CreateDirectMessageRequest)] = "Single target user id; contactability is semantic (DirectMessagesController).",
        [typeof(AddGroupParticipantRequest)] = "Single user id; membership/contactability is semantic.",
        [typeof(MoveChannelCategoryRequest)] = "Single nullable category id; belongs-to-guild is semantic (ChannelsController).",

        // Structural reorder payloads reconciled server-side against real membership/ownership:
        [typeof(ReorderChannelRequest)] = "Reorder payload reconciled against the guild's real channels.",
        [typeof(ReorderRolesRequest)] = "Reorder payload reconciled against the guild's real roles.",
        [typeof(UpdateGuildOrderRequest)] = "Personal guild-rail order reconciled against the caller's real guilds.",

        // Permission-bit longs + a role|user discriminator, validated semantically at the controller:
        [typeof(UpsertChannelOverrideRequest)] = "TargetType + allow/deny bit masks validated in ChannelsController.",
    };

    public static IEnumerable<object[]> RequestTypes() =>
        ApplicationAssembly
            .GetTypes()
            .Where(t =>
                t.Namespace == RequestsNamespace
                && t.IsClass
                && !t.IsAbstract
                && t.Name.EndsWith("Request", StringComparison.Ordinal)
            )
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(RequestTypes))]
    public void EveryRequestType_HasValidatorOrIsAllowListed(Type requestType)
    {
        var hasValidator = ValidatorExistsFor(requestType);
        var isAllowListed = NoValidatorByDesign.ContainsKey(requestType);

        (hasValidator || isAllowListed)
            .Should()
            .BeTrue(
                $"request type {requestType.Name} must have an AbstractValidator<{requestType.Name}> or "
                    + "be added to NoValidatorByDesign with a documented reason (audit A4: validators are "
                    + "opt-in, so an unlisted DTO silently ships with no validation)."
            );
    }

    [Fact]
    public void AllowList_HasNoStaleOrRedundantEntries()
    {
        foreach (var (type, reason) in NoValidatorByDesign)
        {
            type.Namespace.Should().Be(RequestsNamespace, $"{type.Name} on the allow-list must still be a request DTO");
            reason.Should().NotBeNullOrWhiteSpace($"{type.Name} needs a documented reason for having no validator");
            ValidatorExistsFor(type)
                .Should()
                .BeFalse(
                    $"{type.Name} now has a validator — remove it from NoValidatorByDesign so the allow-list "
                        + "stays a list of genuinely un-validated types."
                );
        }
    }

    private static bool ValidatorExistsFor(Type requestType) =>
        ApplicationAssembly
            .GetTypes()
            .Any(t => t is { IsClass: true, IsAbstract: false } && ValidatedType(t) == requestType);

    /// <summary>The T of an AbstractValidator&lt;T&gt; anywhere up the inheritance chain, else null.</summary>
    private static Type? ValidatedType(Type candidate)
    {
        for (var t = candidate.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AbstractValidator<>))
                return t.GetGenericArguments()[0];
        }

        return null;
    }
}
