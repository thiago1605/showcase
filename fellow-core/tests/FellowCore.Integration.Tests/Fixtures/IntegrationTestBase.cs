using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Integration.Tests.Fixtures;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    protected HttpClient Client { get; private set; } = null!;
    protected Guid TenantId { get; private set; }
    protected Guid SellerId { get; private set; }
    protected CustomWebApplicationFactory Factory => _factory;

    public async Task InitializeAsync()
    {
        Client = _factory.CreateDefaultClient(new AutoIdempotencyKeyHandler());

        var (tenant, seller) = await TestDataHelper.SeedTenantAndSellerAsync(_factory.Services);
        TenantId = tenant.Id;
        SellerId = seller.Id;

        Client.DefaultRequestHeaders.Add("X-Api-Key", TestDataHelper.TestApiKey);
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    protected HttpClient CreateUnauthenticatedClient()
        => _factory.CreateDefaultClient(new AutoIdempotencyKeyHandler());

    /// <summary>
    /// Cria um HttpClient autenticado via JWT (login com user OWNER do tenant).
    /// Necessário pra endpoints que exigem [Authorize(Roles=...)] e não aceitam API key
    /// (ex: AuditLogsController, qualquer controller restrito a operadores).
    /// </summary>
    protected async Task<HttpClient> CreateJwtClientAsync(UserRole role = UserRole.OWNER)
    {
        await TestDataHelper.SeedUserAsync(_factory.Services, TenantId, role);

        var anon = _factory.CreateDefaultClient(new AutoIdempotencyKeyHandler());
        var loginResponse = await anon.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        loginResponse.EnsureSuccessStatusCode();

        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginJson.GetProperty("data").GetProperty("accessToken").GetString()!;

        var jwtClient = _factory.CreateDefaultClient(new AutoIdempotencyKeyHandler());
        jwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return jwtClient;
    }
}

/// <summary>
/// Automatically adds an Idempotency-Key header to POST requests that don't already have one.
/// This ensures integration tests exercise the IdempotencyMiddleware without requiring each
/// test to manually add the header.
/// </summary>
internal class AutoIdempotencyKeyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && !request.Headers.Contains("Idempotency-Key"))
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        return base.SendAsync(request, cancellationToken);
    }
}
