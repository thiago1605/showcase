using System.Net;
using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Processors;

public class TenantWebhookProcessorTests
{
    private readonly IWebhookEndpointRepository _webhookEndpointRepository = Substitute.For<IWebhookEndpointRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly ICustomerRepository _customerRepository = Substitute.For<ICustomerRepository>();
    private readonly IProductRepository _productRepository = Substitute.For<IProductRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILogger<TenantWebhookProcessor> _logger = Substitute.For<ILogger<TenantWebhookProcessor>>();
    private readonly TenantWebhookProcessor _sut;

    public TenantWebhookProcessorTests()
    {
        // DecryptAsync returns the input unchanged — test secrets are not actually encrypted
        _securityService.DecryptAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        // GetByIdAsync retorna null por default — enrichment é best-effort e tolera null.
        _sut = new TenantWebhookProcessor(
            _webhookEndpointRepository,
            _transactionRepository,
            _customerRepository,
            _productRepository,
            _sellerRepository,
            _securityService,
            _httpClientFactory,
            Substitute.For<IAppMetrics>(),
            _logger);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDoNothing_WhenNoActiveEndpoints()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId);

        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint>());

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
        await _webhookEndpointRepository.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessAsync_ShouldDeliverWebhook_ToActiveEndpoint()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId);

        var endpoint = CreateEndpoint(tenantId, "https://example.com/hook");
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint });

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.com/hook");

        endpoint.Deliveries.Should().HaveCount(1);
        var delivery = endpoint.Deliveries.First();
        delivery.Success.Should().BeTrue();

        await _webhookEndpointRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessAsync_ShouldRecordFailedDelivery_WhenHttpReturnsError()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId);

        var endpoint = CreateEndpoint(tenantId, "https://example.com/hook");
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint });

        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        endpoint.Deliveries.Should().HaveCount(1);
        var delivery = endpoint.Deliveries.First();
        delivery.Success.Should().BeFalse();
        delivery.LastError.Should().Contain("HTTP 500");

        await _webhookEndpointRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessAsync_ShouldRecordFailedDelivery_WhenHttpThrowsException()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId);

        var endpoint = CreateEndpoint(tenantId, "https://example.com/hook");
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint });

        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        endpoint.Deliveries.Should().HaveCount(1);
        var delivery = endpoint.Deliveries.First();
        delivery.Success.Should().BeFalse();
        delivery.LastError.Should().Contain("Connection refused");

        await _webhookEndpointRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessAsync_ShouldSkipEndpoint_WhenEventNotInFilteredList()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId, TransactionStatus.CAPTURED);

        // Endpoint only listens for "transaction.declined"
        var endpoint = CreateEndpoint(tenantId, "https://example.com/hook", events: ["transaction.declined"]);
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint });

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.RequestCount.Should().Be(0);
        endpoint.Deliveries.Should().HaveCount(0);
        await _webhookEndpointRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessAsync_ShouldDeliverToAllMatchingEndpoints()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId, TransactionStatus.CAPTURED);

        var endpoint1 = CreateEndpoint(tenantId, "https://example.com/hook1");
        var endpoint2 = CreateEndpoint(tenantId, "https://example.com/hook2");
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint1, endpoint2 });

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        // Return a new HttpClient each call since the processor disposes each one via 'using'
        _httpClientFactory.CreateClient("WebhookClient")
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.RequestCount.Should().Be(2);
        endpoint1.Deliveries.Should().HaveCount(1);
        endpoint2.Deliveries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ProcessAsync_ShouldIncludeSignatureAndEventHeaders()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var job = CreateJobData(tenantId);

        var endpoint = CreateEndpoint(tenantId, "https://example.com/hook");
        _webhookEndpointRepository.GetActiveForSellerEventAsync(tenantId, Arg.Any<Guid>())
            .Returns(new List<WebhookEndpoint> { endpoint });

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Contains("X-Webhook-Signature").Should().BeTrue();
        handler.LastRequest!.Headers.Contains("X-Webhook-Event").Should().BeTrue();
        handler.LastRequest!.Headers.Contains("User-Agent").Should().BeTrue();
    }

    #region Helpers

    private static NotificationJobData CreateJobData(Guid tenantId, TransactionStatus status = TransactionStatus.CAPTURED) =>
        new(
            TenantId: tenantId,
            SellerId: Guid.NewGuid(),
            TransactionId: Guid.NewGuid(),
            Status: status,
            NetAmount: 98m,
            ProviderTxId: "prov_tx_001",
            PaymentType: PaymentType.PIX);

    private static WebhookEndpoint CreateEndpoint(Guid tenantId, string url, List<string>? events = null)
    {
        var result = WebhookEndpoint.Create(tenantId, url, "secret-32-chars-long-enough!!!!!!!", events);
        return result.Value;
    }

    /// <summary>
    /// Fake HttpMessageHandler for capturing requests and returning configurable responses.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _statusCode;
        private readonly Exception? _exception;
        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public FakeHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequest = request;

            if (_exception != null)
                throw _exception;

            return new HttpResponseMessage(_statusCode!.Value)
            {
                Content = new StringContent("{}")
            };
        }
    }

    #endregion
}
