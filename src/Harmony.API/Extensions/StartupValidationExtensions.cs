using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Harmony.API.Extensions;

/// <summary>
/// Fail-fast validation of the secrets/connection strings a production deployment cannot run
/// correctly without. Runs at startup, before anything consumes them.
/// </summary>
/// <remarks>
/// <para>
/// The problem this closes (audit A14): <c>appsettings.json</c> ships these keys as empty-string
/// placeholders (the real values arrive from environment variables / user-secrets). Because the keys
/// <em>exist</em> as <c>""</c>, code like <c>Encoding.UTF8.GetBytes(Configuration["Jwt:Key"]!)</c>
/// happily boots with an <em>empty HMAC signing key</em> instead of throwing — a misconfigured deploy
/// then serves traffic with broken auth/storage, discovered only by users. This turns that silent,
/// security-relevant failure into a clear boot-time error.
/// </para>
/// <para>
/// Deliberately scoped to non-Development, non-Test environments: local dev and the integration test
/// host legitimately boot with placeholder secrets (Mailpit, no LiveKit/WebPush, etc.), so enforcing
/// there would break them. Optional-feature secrets (LiveKit, WebPush, Google, SMTP) are intentionally
/// NOT required — an empty value there disables a feature, it does not silently break security or the
/// core data path. Only the security-critical (JWT) and core-infrastructure (datastore connection
/// strings, object storage) secrets are fatal.
/// </para>
/// </remarks>
public static class StartupValidationExtensions
{
    // Security-critical + core-infrastructure config a production instance cannot serve correctly
    // without. Order is presentation order in the error message.
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

    /// <summary>
    /// Throws if any required secret/connection string is missing or empty in a production-like
    /// environment. No-op in Development and Test (they run with placeholders by design).
    /// </summary>
    /// <exception cref="StartupConfigurationException">
    /// Thrown listing every missing key, so a misconfigured deploy fails at boot rather than at first
    /// use. Never reaches the HTTP pipeline — the app exits before serving traffic.
    /// </exception>
    public static void ValidateRequiredConfiguration(
        this IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
            return;

        var missing = RequiredKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToList();

        if (missing.Count == 0)
            return;

        throw new StartupConfigurationException(
            $"Refusing to start in the '{environment.EnvironmentName}' environment: the following "
                + $"required configuration values are missing or empty — {string.Join(", ", missing)}. "
                + "Provide them via environment variables (e.g. Jwt__Key) or user-secrets."
        );
    }
}

/// <summary>
/// Raised at startup when a production-like deployment is missing a required secret. A distinct type
/// (rather than a generic <see cref="InvalidOperationException"/>) so it is unmistakably a
/// configuration/boot failure and never conflated with request-path validation.
/// </summary>
public sealed class StartupConfigurationException(string message) : Exception(message);
