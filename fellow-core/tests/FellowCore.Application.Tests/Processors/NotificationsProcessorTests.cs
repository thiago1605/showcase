using System.Net;
using System.Text.Json;
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

public class NotificationsProcessorTests
{
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILogger<NotificationsProcessor> _logger = Substitute.For<ILogger<NotificationsProcessor>>();
    private readonly NotificationsProcessor _sut;

    public NotificationsProcessorTests()
    {
        // H16: WebhookSecret is now encrypted at rest; mock decryption to return the plaintext value
        _securityService.DecryptAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _sut = new NotificationsProcessor(_sellerRepository, _securityService, _httpClientFactory, _logger);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDeliverWebhook_WhenSellerHasWebhookUrl()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var job = CreateJobData(tenantId, sellerId);

        var seller = CreateSellerWithWebhook(tenantId, "https://example.com/webhook");

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.com/webhook");
        handler.LastRequestContent.Should().Contain("transaction.captured");
    }

    [Fact]
    public async Task ProcessAsync_ShouldReturn_WhenSellerNotFound()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var job = CreateJobData(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns((Seller?)null);

        // Act & Assert — should not throw
        await _sut.ProcessAsync(job);

        // No HTTP call should have been made
        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldReturn_WhenSellerHasNoWebhookUrl()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var job = CreateJobData(tenantId, sellerId);

        var seller = CreateSellerWithoutWebhook(tenantId);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        // Act & Assert — should not throw
        await _sut.ProcessAsync(job);

        _httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrow_WhenWebhookReturnsNonSuccessStatus()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var job = CreateJobData(tenantId, sellerId);

        var seller = CreateSellerWithWebhook(tenantId, "https://example.com/webhook");
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        var act = () => _sut.ProcessAsync(job);

        // Assert — should rethrow for Hangfire retry
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ProcessAsync_ShouldIncludeSignatureHeader()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var job = CreateJobData(tenantId, sellerId);

        var seller = CreateSellerWithWebhook(tenantId, "https://example.com/webhook");
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("WebhookClient").Returns(httpClient);

        // Act
        await _sut.ProcessAsync(job);

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Contains("X-Signature").Should().BeTrue();
        handler.LastRequest!.Headers.Contains("X-Webhook-Event").Should().BeTrue();
        handler.LastRequest!.Headers.Contains("User-Agent").Should().BeTrue();
    }

    #region Helpers

    private static NotificationJobData CreateJobData(Guid tenantId, Guid sellerId) =>
        new(
            TenantId: tenantId,
            SellerId: sellerId,
            TransactionId: Guid.NewGuid(),
            Status: TransactionStatus.CAPTURED,
            NetAmount: 98m,
            ProviderTxId: "prov_tx_001",
            PaymentType: PaymentType.PIX);

    private static Seller CreateSellerWithWebhook(Guid tenantId, string webhookUrl)
    {
        var seller = Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "seller@test.com",
            webhookSecret: "test-secret-32-chars-long-enough!");

        seller.Update(tradeName: null, email: null, mobilePhone: null, pixKey: null, webhookUrl: webhookUrl);
        return seller;
    }

    private static Seller CreateSellerWithoutWebhook(Guid tenantId)
    {
        return Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "seller@test.com",
            webhookSecret: "test-secret-32-chars-long-enough!");
    }

    /// <summary>
    /// Fake HttpMessageHandler that captures the request and returns a configurable response.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastRequestContent { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequest = request;

            if (request.Content != null)
                LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}")
            };
        }
    }

    #endregion
}
