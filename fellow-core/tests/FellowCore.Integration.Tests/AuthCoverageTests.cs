using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FellowCore.Integration.Tests;

/// <summary>
/// Ensures every controller has an explicit auth policy at class or action level.
/// Prevents new controllers/actions from being accidentally deployed without auth.
/// </summary>
public class AuthCoverageTests
{
    private static readonly Type[] AuthAttributes =
    [
        typeof(AuthorizeAttribute),
        typeof(AllowAnonymousAttribute),
        // Custom attributes defined in FellowCore.Api
        Type.GetType("FellowCore.Api.Auth.ApiKeyAuthAttribute, FellowCore.Api")!,
        Type.GetType("FellowCore.Api.Auth.MasterKeyAuthAttribute, FellowCore.Api")!,
        // AuthOrApiKeyAuth aceita JWT (portal) OU API key (server-to-server) — usado
        // em endpoints expostos pra ambos os fluxos (dashboard, payments, etc.).
        Type.GetType("FellowCore.Api.Auth.AuthOrApiKeyAuthAttribute, FellowCore.Api")!
    ];

    private static IEnumerable<Type> GetAllControllers()
    {
        var apiAssembly = typeof(Program).Assembly;
        return apiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));
    }

    [Fact]
    public void AllControllers_HaveExplicitAuthPolicy()
    {
        var controllers = GetAllControllers().ToList();
        controllers.Should().NotBeEmpty("there should be at least one controller in the API");

        var unprotected = new List<string>();

        foreach (var controller in controllers)
        {
            bool hasClassLevelAuth = controller.GetCustomAttributes(true)
                .Any(attr => AuthAttributes.Any(authType => authType.IsInstanceOfType(attr)));

            if (hasClassLevelAuth) continue;

            // If no class-level auth, check each public action method
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a =>
                    a is HttpGetAttribute or HttpPostAttribute or HttpPutAttribute
                    or HttpDeleteAttribute or HttpPatchAttribute));

            foreach (var action in actions)
            {
                bool hasActionLevelAuth = action.GetCustomAttributes(true)
                    .Any(attr => AuthAttributes.Any(authType => authType.IsInstanceOfType(attr)));

                if (!hasActionLevelAuth)
                {
                    unprotected.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        unprotected.Should().BeEmpty(
            "every controller or action must have an explicit auth policy " +
            "([ApiKeyAuth], [MasterKeyAuth], [Authorize], or [AllowAnonymous]). " +
            $"Unprotected: {string.Join(", ", unprotected)}");
    }

    [Fact]
    public void AllControllers_AreDiscovered()
    {
        // Sanity check: we should discover a reasonable number of controllers
        var controllers = GetAllControllers().ToList();
        controllers.Count.Should().BeGreaterOrEqualTo(15,
            "expected at least 15 controllers in the API project");
    }
}
