using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using FellowCore.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests;

public class ReconciliationEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Auth & RBAC ────────────────────────────────────────────────────

    [Fact]
    public async Task GetRuns_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/reconciliation/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRuns_WithApiKeyOnly_Returns401()
    {
        var response = await Client.GetAsync("/api/v1/reconciliation/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRuns_WithViewerRole_Returns403()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.VIEWER);
        var response = await jwtClient.GetAsync("/api/v1/reconciliation/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetRuns_WithOwnerRole_ReturnsOk()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);
        var response = await jwtClient.GetAsync("/api/v1/reconciliation/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRuns_WithFinanceRole_ReturnsOk()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.FINANCE, "finance@test.com");
        var response = await jwtClient.GetAsync("/api/v1/reconciliation/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Runs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRuns_ReturnsSeededRuns()
    {
        await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.GetAsync("/api/v1/reconciliation/runs?limit=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        data[0].GetProperty("runType").GetString().Should().Be("BATCH");
    }

    // ── Tenant Isolation ───────────────────────────────────────────────

    [Fact]
    public async Task GetRuns_DoesNotLeakOtherTenantData()
    {
        await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.GetAsync("/api/v1/reconciliation/runs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await DeserializeAsync(response)).GetProperty("data");
        foreach (var run in data.EnumerateArray())
        {
            run.GetProperty("tenantId").GetString().Should().Be(TenantId.ToString());
        }
    }

    // ── Issues ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIssues_ReturnsSeededIssues()
    {
        await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.GetAsync("/api/v1/reconciliation/issues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        data[0].GetProperty("resolution").GetString().Should().Be("OPEN");
    }

    [Fact]
    public async Task GetIssues_FilterBySeverity()
    {
        await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.GetAsync("/api/v1/reconciliation/issues?severity=CRITICAL");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        foreach (var issue in data.EnumerateArray())
        {
            issue.GetProperty("severity").GetString().Should().Be("CRITICAL");
        }
    }

    // ── Investigate ────────────────────────────────────────────────────

    [Fact]
    public async Task Investigate_ValidIssue_ReturnsOk()
    {
        var issueId = await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsync($"/api/v1/reconciliation/issues/{issueId}/investigate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        data.GetProperty("resolution").GetString().Should().Be("INVESTIGATING");
    }

    [Fact]
    public async Task Investigate_NonExistent_Returns404()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsync($"/api/v1/reconciliation/issues/{Guid.NewGuid()}/investigate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Investigate_AlreadyResolved_Returns400()
    {
        var issueId = await SeedReconciliationDataAsync(resolved: true);
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsync($"/api/v1/reconciliation/issues/{issueId}/investigate", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Resolve ────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_ValidIssue_ReturnsOk()
    {
        var issueId = await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsJsonAsync(
            $"/api/v1/reconciliation/issues/{issueId}/resolve",
            new { notes = "Fixed by manual adjustment" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        data.GetProperty("resolution").GetString().Should().Be("RESOLVED");
        data.GetProperty("resolvedBy").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Resolve_NonExistent_Returns404()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsJsonAsync(
            $"/api/v1/reconciliation/issues/{Guid.NewGuid()}/resolve",
            new { notes = "test" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_AlreadyResolved_Returns400()
    {
        var issueId = await SeedReconciliationDataAsync(resolved: true);
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsJsonAsync(
            $"/api/v1/reconciliation/issues/{issueId}/resolve",
            new { notes = "duplicate" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Dismiss ────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_ValidIssue_ReturnsOk()
    {
        var issueId = await SeedReconciliationDataAsync();
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsJsonAsync(
            $"/api/v1/reconciliation/issues/{issueId}/dismiss",
            new { notes = "False positive — rounding difference" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await DeserializeAsync(response)).GetProperty("data");
        data.GetProperty("resolution").GetString().Should().Be("DISMISSED");
    }

    [Fact]
    public async Task Dismiss_OtherTenantIssue_Returns404()
    {
        var jwtClient = await CreateJwtClientAsync(UserRole.OWNER);

        var response = await jwtClient.PostAsJsonAsync(
            $"/api/v1/reconciliation/issues/{Guid.NewGuid()}/dismiss",
            new { notes = "should not work" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<HttpClient> CreateJwtClientAsync(UserRole role = UserRole.OWNER, string? email = null)
    {
        var userEmail = email ?? TestDataHelper.TestUserEmail;
        await SeedUserWithRoleAsync(role, userEmail);

        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = userEmail, password = TestDataHelper.TestUserPassword });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "login should succeed");

        var loginBody = await DeserializeAsync(loginResponse);
        var accessToken = loginBody.GetProperty("data").GetProperty("accessToken").GetString()!;

        var jwtClient = CreateUnauthenticatedClient();
        jwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return jwtClient;
    }

    private async Task SeedUserWithRoleAsync(UserRole role, string email)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<FellowCore.Application.Modules.Auth.Interfaces.IPasswordHasher>();

        if (!db.Users.Any(u => u.Email == email))
        {
            var user = User.Create(
                name: $"Test {role}",
                email: email,
                passwordHash: hasher.Hash(TestDataHelper.TestUserPassword),
                role: role,
                tenantId: TenantId);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
    }

    private async Task<Guid> SeedReconciliationDataAsync(bool resolved = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var run = ReconciliationRun.Create(TenantId, "BATCH", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        var issue = run.AddIssue(
            ReconciliationIssueType.AMOUNT_MISMATCH, "CRITICAL",
            internalId: Guid.NewGuid().ToString(), externalId: "pi_test_123",
            expectedCents: 10000, actualCents: 9500,
            description: "Transaction amount differs by 500 cents");

        run.Complete(transactionsChecked: 10, payoutsChecked: 2, ledgerAccountsChecked: 4,
            issuesFound: 1, criticalIssues: 1, platformDriftCents: 500);

        if (resolved) issue.Resolve("Pre-resolved for test", "test-user");

        db.Set<ReconciliationRun>().Add(run);
        await db.SaveChangesAsync();
        return issue.Id;
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
