using System.Reflection;
using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FellowCore.Application.Tests.Services;

public class WebhookRetryProcessorTests
{
    private readonly IWebhookDeliveryRepository _deliveryRepo = Substitute.For<IWebhookDeliveryRepository>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ILogger<WebhookRetryProcessor> _logger = Substitute.For<ILogger<WebhookRetryProcessor>>();
    private readonly WebhookRetryProcessor _sut;

    public WebhookRetryProcessorTests()
    {
        _securityService.DecryptAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _sut = new WebhookRetryProcessor(_deliveryRepo, _securityService, _httpClientFactory, _emailService, _configuration, _logger);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WithNoPending_DoesNothing()
    {
        _deliveryRepo.GetPendingRetriesAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<WebhookDelivery>());

        await _sut.ProcessPendingRetriesAsync();

        await _deliveryRepo.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WithSuccessfulRetry_MarksAsSucceeded()
    {
        var (delivery, _) = CreateFailedDelivery();

        _deliveryRepo.GetPendingRetriesAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<WebhookDelivery> { delivery });

        SetupHttpClient(System.Net.HttpStatusCode.OK);

        await _sut.ProcessPendingRetriesAsync();

        delivery.Status.Should().Be(DeliveryStatus.SUCCEEDED);
        delivery.RetryCount.Should().Be(1);
        await _deliveryRepo.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WithFailedRetry_SchedulesNextRetry()
    {
        var (delivery, _) = CreateFailedDelivery();

        _deliveryRepo.GetPendingRetriesAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<WebhookDelivery> { delivery });

        SetupHttpClient(System.Net.HttpStatusCode.InternalServerError);

        await _sut.ProcessPendingRetriesAsync();

        delivery.Status.Should().Be(DeliveryStatus.PENDING_RETRY);
        delivery.RetryCount.Should().Be(1);
        delivery.NextRetryAt.Should().NotBeNull();
        await _deliveryRepo.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_WhenCancelled_StopsProcessing()
    {
        var (delivery, _) = CreateFailedDelivery();

        _deliveryRepo.GetPendingRetriesAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<WebhookDelivery> { delivery });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _sut.ProcessPendingRetriesAsync(cts.Token);

        delivery.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessPendingRetriesAsync_DisabledEndpoint_RecordsFailure()
    {
        var (delivery, endpoint) = CreateFailedDelivery();
        endpoint.Disable();

        _deliveryRepo.GetPendingRetriesAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<WebhookDelivery> { delivery });

        await _sut.ProcessPendingRetriesAsync();

        delivery.RetryCount.Should().Be(1);
        delivery.LastError.Should().Contain("desabilitado");
        await _deliveryRepo.Received(1).SaveChangesAsync();
    }

    private (WebhookDelivery Delivery, WebhookEndpoint Endpoint) CreateFailedDelivery()
    {
        var endpoint = WebhookEndpoint.Create(Guid.NewGuid(), "https://example.com/hook", "secret-32chars-ok!!").Value;
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.captured",
            JsonSerializer.SerializeToDocument(new { test = true }), 500, false, 100, "HTTP 500");

        // Simulate EF Core Include by setting the navigation property via reflection
        var endpointProp = typeof(WebhookDelivery).GetProperty("Endpoint")!;
        endpointProp.SetValue(delivery, endpoint);

        return (delivery, endpoint);
    }

    private void SetupHttpClient(System.Net.HttpStatusCode statusCode)
    {
        var handler = new FakeHttpMessageHandler(statusCode);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);
    }

    private class FakeHttpMessageHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
