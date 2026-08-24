using FluentAssertions;
using Harmony.API.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Harmony.IntegrationTests.Architecture;

/// <summary>
/// Verifies the fail-fast startup guard (audit A14): a production-like instance refuses to boot with a
/// missing/empty required secret, while Development and Test boot with placeholders as before.
/// </summary>
public class StartupConfigurationValidationTests
{
    private static readonly string[] RequiredKeys =
    [
        "Jwt:Key",
        "Jwt:Issuer",
        "Jwt:Audience",
        "ConnectionStrings:Postgres",
        "ConnectionStrings:Redis",
        "ConnectionStrings:RabbitMQ",
        "ObjectStorage:Endpoint",
        "ObjectStorage:AccessKey",
        "ObjectStorage:SecretKey",
    ];

    private static IConfiguration ConfigWith(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IDictionary<string, string?> AllRequiredPresent() =>
        RequiredKeys.ToDictionary(k => k, k => (string?)"set");

    [Fact]
    public void Production_WithAllRequiredPresent_DoesNotThrow()
    {
        var config = ConfigWith(AllRequiredPresent());
        var env = new StubEnvironment("Production");

        var act = () => config.ValidateRequiredConfiguration(env);

        act.Should().NotThrow();
    }

    [Fact]
    public void Production_WithEmptyJwtKey_Throws_AndNamesTheKey()
    {
        var values = AllRequiredPresent();
        values["Jwt:Key"] = ""; // the exact silent-empty-signing-key case
        var config = ConfigWith(values);
        var env = new StubEnvironment("Production");

        var act = () => config.ValidateRequiredConfiguration(env);

        act.Should()
            .Throw<StartupConfigurationException>()
            .WithMessage("*Jwt:Key*");
    }

    [Fact]
    public void Production_WithMultipleMissing_ListsThemAll()
    {
        var values = AllRequiredPresent();
        values["Jwt:Key"] = "   "; // whitespace counts as empty
        values["ObjectStorage:SecretKey"] = null;
        var config = ConfigWith(values);
        var env = new StubEnvironment("Production");

        var act = () => config.ValidateRequiredConfiguration(env);

        act.Should()
            .Throw<StartupConfigurationException>()
            .Where(e => e.Message.Contains("Jwt:Key") && e.Message.Contains("ObjectStorage:SecretKey"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void DevelopmentAndTest_WithEverythingEmpty_DoNotThrow(string environmentName)
    {
        // These environments legitimately boot with placeholder secrets (Mailpit, no LiveKit, etc.).
        var config = ConfigWith(RequiredKeys.ToDictionary(k => k, _ => (string?)""));
        var env = new StubEnvironment(environmentName);

        var act = () => config.ValidateRequiredConfiguration(env);

        act.Should().NotThrow();
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Harmony.API";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
