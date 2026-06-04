using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Harmony.IntegrationTests.Architecture;

public class ArchitectureTests
{
    private static readonly Assembly DomainAssembly =
        typeof(Harmony.Domain.Domain.Entities.User).Assembly;
    private static readonly Assembly ApplicationAssembly =
        typeof(Harmony.Application.Services.AuthService).Assembly;
    private static readonly Assembly InfrastructureAssembly =
        typeof(Harmony.Infrastructure.Postgres.HarmonyDbContext).Assembly;
    private static readonly Assembly ApiAssembly =
        typeof(Harmony.API.Controllers.AuthController).Assembly;

    [Fact]
    public void Domain_Should_Not_HaveDependencyOn_OtherProjects()
    {
        var result = Types
            .InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAll("Harmony.Application", "Harmony.Infrastructure", "Harmony.API")
            .GetResult();

        result
            .IsSuccessful.Should()
            .BeTrue(
                "Domain layer should never depend on higher-level layers (Application, Infrastructure, API)."
            );
    }

    [Fact]
    public void Application_Should_Not_HaveDependencyOn_InfrastructureOrApi()
    {
        var result = Types
            .InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAll("Harmony.Infrastructure", "Harmony.API")
            .GetResult();

        result
            .IsSuccessful.Should()
            .BeTrue(
                "Application layer should only depend on Domain, never on Infrastructure or API."
            );
    }

    [Fact]
    public void Infrastructure_Should_Not_HaveDependencyOn_Api()
    {
        var result = Types
            .InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOn("Harmony.API")
            .GetResult();

        result
            .IsSuccessful.Should()
            .BeTrue("Infrastructure layer should not depend on the API layer.");
    }

    [Fact]
    public void Controllers_Should_Not_HaveDependencyOn_InfrastructureOrDbContext()
    {
        // Controllers should communicate strictly via interfaces / Application abstractions,
        // never directly with Entity Framework DbContexts or raw infrastructure models.
        var result = Types
            .InAssembly(ApiAssembly)
            .That()
            .HaveNameEndingWith("Controller")
            .ShouldNot()
            .HaveDependencyOnAll("Harmony.Infrastructure.Postgres", "Microsoft.EntityFrameworkCore")
            .GetResult();

        result
            .IsSuccessful.Should()
            .BeTrue(
                "Controllers must not directly depend on DbContext or Postgres Infrastructure concrete classes."
            );
    }
}
