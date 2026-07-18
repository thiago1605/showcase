using System.Text.Json;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FluentAssertions;

namespace FellowCore.Domain.Tests.Entities;

public class WebhookDeliveryTests
{
    private static WebhookEndpoint CreateEndpoint()
    {
        return WebhookEndpoint.Create(Guid.NewGuid(), "https://example.com/webhook", "secret-key-32chars-ok!!").Value;
    }

    [Fact]
    public void RecordDelivery_WhenSuccess_StatusIsSucceeded()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.captured",
            JsonSerializer.SerializeToDocument(new { test = true }), 200, true, 50);

        delivery.Status.Should().Be(DeliveryStatus.SUCCEEDED);
        delivery.NextRetryAt.Should().BeNull();
        delivery.RetryCount.Should().Be(0);
        delivery.Success.Should().BeTrue();
    }

    [Fact]
    public void RecordDelivery_WhenFailed_StatusIsPendingRetry()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { test = true }), 500, false, 100, "HTTP 500");

        delivery.Status.Should().Be(DeliveryStatus.PENDING_RETRY);
        delivery.NextRetryAt.Should().NotBeNull();
        delivery.NextRetryAt.Should().BeAfter(DateTime.UtcNow);
        delivery.RetryCount.Should().Be(0);
        delivery.LastError.Should().Be("HTTP 500");
        delivery.CanRetry.Should().BeTrue();
    }

    [Fact]
    public void RecordRetryAttempt_WhenSuccess_StatusIsSucceeded()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { }), 500, false, 100);

        delivery.RecordRetryAttempt(200, true, 80);

        delivery.Status.Should().Be(DeliveryStatus.SUCCEEDED);
        delivery.RetryCount.Should().Be(1);
        delivery.Success.Should().BeTrue();
        delivery.NextRetryAt.Should().BeNull();
        delivery.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void RecordRetryAttempt_WhenFailed_SchedulesNextRetry()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { }), null, false, 0, "timeout");

        delivery.RecordRetryAttempt(null, false, 0, "timeout again");

        delivery.Status.Should().Be(DeliveryStatus.PENDING_RETRY);
        delivery.RetryCount.Should().Be(1);
        delivery.NextRetryAt.Should().NotBeNull();
        delivery.CanRetry.Should().BeTrue();
    }

    [Fact]
    public void RecordRetryAttempt_AfterMaxRetries_StatusIsFailed()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { }), 500, false, 100);

        for (int i = 0; i < WebhookDelivery.MaxRetryAttempts; i++)
        {
            delivery.RecordRetryAttempt(500, false, 100, $"Attempt {i + 1} failed");
        }

        delivery.Status.Should().Be(DeliveryStatus.FAILED);
        delivery.RetryCount.Should().Be(WebhookDelivery.MaxRetryAttempts);
        delivery.NextRetryAt.Should().BeNull();
        delivery.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void RecordRetryAttempt_ExponentialBackoff_DelaysIncrease()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { }), 500, false, 100);

        var previousRetryAt = delivery.NextRetryAt!.Value;

        delivery.RecordRetryAttempt(500, false, 100, "fail");
        var secondRetryAt = delivery.NextRetryAt!.Value;

        delivery.RecordRetryAttempt(500, false, 100, "fail");
        var thirdRetryAt = delivery.NextRetryAt!.Value;

        // Each retry should be further in the future than the previous
        var gap1 = (secondRetryAt - DateTime.UtcNow).TotalSeconds;
        var gap2 = (thirdRetryAt - DateTime.UtcNow).TotalSeconds;
        gap2.Should().BeGreaterThan(gap1);
    }

    [Fact]
    public void RecordDelivery_WhenSuccess_CanRetryIsFalse()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.captured",
            JsonSerializer.SerializeToDocument(new { }), 200, true, 30);

        delivery.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void ResetForManualRetry_ResetsCounterAndSchedulesImmediate()
    {
        var endpoint = CreateEndpoint();
        var delivery = endpoint.RecordDelivery("evt-1", "transaction.created",
            JsonSerializer.SerializeToDocument(new { }), 500, false, 100);

        // Exhaust all retries
        for (int i = 0; i < WebhookDelivery.MaxRetryAttempts; i++)
            delivery.RecordRetryAttempt(500, false, 100, "fail");

        delivery.Status.Should().Be(DeliveryStatus.FAILED);
        delivery.CanRetry.Should().BeFalse();

        delivery.ResetForManualRetry();

        delivery.Status.Should().Be(DeliveryStatus.PENDING_RETRY);
        delivery.RetryCount.Should().Be(0);
        delivery.NextRetryAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        delivery.LastError.Should().BeNull();
        delivery.CanRetry.Should().BeTrue();
    }
}
