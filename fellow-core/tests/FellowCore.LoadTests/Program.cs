using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NBomber.CSharp;

// -------------------------------------------------------------------
// FellowCore Load Tests
//
// Usage:
//   dotnet run                           → padrão: http://localhost:5000
//   dotnet run -- --base-url http://…    → aponta para outro host
//   dotnet run -- --api-key pk_live_…    → usa chave de API real
//
// Cenários:
//   1. health_check      – GET /health (sem auth)  → baseline do servidor
//   2. list_transactions – GET /api/v1/transactions (com X-Api-Key)
//   3. rate_limit_check  – rajada POST → espera 429 após limite
//   4. signalr_connect   – conexão e reconexão SignalR em massa
// -------------------------------------------------------------------

var baseUrl = GetArg(args, "--base-url") ?? "http://localhost:5000";
var apiKey  = GetArg(args, "--api-key")  ?? "pk_test_load_test_key";

Console.WriteLine($"Target : {baseUrl}");
Console.WriteLine($"Api-Key: {apiKey[..Math.Min(10, apiKey.Length)]}…");
Console.WriteLine();

// ── Scenario 1: Health Check ────────────────────────────────────────
var healthScenario = Scenario.Create("health_check", async context =>
{
    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var response = await client.GetAsync("/health");
    var status   = ((int)response.StatusCode).ToString();

    return response.StatusCode == HttpStatusCode.OK
        ? Response.Ok(statusCode: status)
        : Response.Fail(statusCode: status);
})
.WithWarmUpDuration(TimeSpan.FromSeconds(3))
.WithLoadSimulations(
    Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

// ── Scenario 2: List Transactions (authenticated) ────────────────────
var listTxnsScenario = Scenario.Create("list_transactions", async context =>
{
    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

    var response = await client.GetAsync("/api/v1/transactions?page=1&pageSize=20");
    var status   = ((int)response.StatusCode).ToString();

    // 200 = ok, 401/403 = expected when using a dummy key in non-dev
    return response.StatusCode is HttpStatusCode.OK
                               or HttpStatusCode.Unauthorized
                               or HttpStatusCode.Forbidden
        ? Response.Ok(statusCode: status)
        : Response.Fail(statusCode: status);
})
.WithWarmUpDuration(TimeSpan.FromSeconds(3))
.WithLoadSimulations(
    Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

// ── Scenario 3: Rate-Limit Verification ─────────────────────────────
// Floods POST /api/v1/transactions até provocar 429.
// Em produção, o limite é 100 req/min por IP.
var rateLimitScenario = Scenario.Create("rate_limit_check", async context =>
{
    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

    var body = JsonSerializer.Serialize(new
    {
        amount      = 1000,
        paymentType = "PIX",
        description = "load-test",
        sellerId    = Guid.NewGuid()
    });

    var content  = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/api/v1/transactions", content);
    var status   = ((int)response.StatusCode).ToString();

    // Qualquer resposta é registrada — objetivo é ver % de 429 no relatório
    return Response.Ok(statusCode: status);
})
.WithWarmUpDuration(TimeSpan.FromSeconds(1))
.WithLoadSimulations(
    // Burst de 200 req/s por 10s → deve provocar 429 após limite de 100/min
    Simulation.Inject(rate: 200, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10))
);

// ── Scenario 4: SignalR Connection Stress ───────────���──────────────
// Testa conexão/reconexão massiva ao hub de notificações.
// Requer JWT válido para autenticação — obtido via login.
var jwtToken = GetArg(args, "--jwt") ?? "";

var signalRScenario = Scenario.Create("signalr_connect", async context =>
{
    try
    {
        var hubUrl = $"{baseUrl.TrimEnd('/')}/hubs/notifications";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrEmpty(jwtToken))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(jwtToken);
            })
            .Build();

        int messagesReceived = 0;
        connection.On<object>("Notification", _ => Interlocked.Increment(ref messagesReceived));

        await connection.StartAsync();

        // Mantém conexão por 2 segundos simulando cliente real
        await Task.Delay(2000);

        await connection.StopAsync();
        await connection.DisposeAsync();

        return Response.Ok(statusCode: "200", sizeBytes: messagesReceived);
    }
    catch (Exception ex)
    {
        return Response.Fail(statusCode: "500", message: ex.Message);
    }
})
.WithWarmUpDuration(TimeSpan.FromSeconds(2))
.WithLoadSimulations(
    // 10 conexões/s por 30s = até 300 conexões simultâneas (cada uma dura ~2s)
    Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
);

NBomberRunner
    .RegisterScenarios(healthScenario, listTxnsScenario, rateLimitScenario, signalRScenario)
    .WithReportFileName("fellowpay_load_report")
    .Run();

// ── Helpers ──────────────────────────────────────────────────────────
static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
