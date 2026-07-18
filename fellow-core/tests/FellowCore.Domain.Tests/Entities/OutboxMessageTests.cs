using FluentAssertions;
using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Tests.Entities;

public class OutboxMessageTests
{
    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var tenantId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow;

        var message = OutboxMessage.Create("TransactionCaptured", "{\"id\":\"tx-001\"}", occurredAt, tenantId);

        message.Id.Should().NotBeEmpty();
        message.TenantId.Should().Be(tenantId);
        message.EventType.Should().Be("TransactionCaptured");
        message.Payload.Should().Be("{\"id\":\"tx-001\"}");
        message.OccurredAt.Should().Be(occurredAt);
        message.ProcessedAt.Should().BeNull();
        message.RetryCount.Should().Be(0);
        message.Error.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldDefaultTenantIdToEmpty()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);

        message.TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void MarkProcessed_ShouldSetProcessedAt()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);

        message.MarkProcessed();

        message.ProcessedAt.Should().NotBeNull();
        message.ProcessedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void MarkFailed_ShouldIncrementRetryCountAndSetError()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);

        message.MarkFailed("Connection timeout");

        message.RetryCount.Should().Be(1);
        message.Error.Should().Be("Connection timeout");
        message.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public void MarkFailed_ShouldIncrementRetryCount_OnMultipleCalls()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);

        message.MarkFailed("First error");
        message.MarkFailed("Second error");
        message.MarkFailed("Third error");

        message.RetryCount.Should().Be(3);
        message.Error.Should().Be("Third error");
    }

    [Fact]
    public void MarkDeadLetter_ShouldSetProcessedAtAndPrefixError()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);
        message.MarkFailed("timeout");
        message.MarkFailed("timeout");
        message.MarkFailed("timeout");

        message.MarkDeadLetter("Max retries exceeded");

        message.ProcessedAt.Should().NotBeNull();
        message.ProcessedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        message.Error.Should().Be("[DLQ] Max retries exceeded");
    }

    [Fact]
    public void MarkDeadLetter_ShouldOverrideError()
    {
        var message = OutboxMessage.Create("TestEvent", "{}", DateTime.UtcNow);
        message.MarkFailed("Previous error");

        message.MarkDeadLetter("Final failure reason");

        message.Error.Should().Be("[DLQ] Final failure reason");
    }

    [Fact]
    public void FullLifecycle_Create_Fail_Fail_DeadLetter()
    {
        var message = OutboxMessage.Create("PaymentCaptured", "{\"amount\":100}", DateTime.UtcNow, Guid.NewGuid());

        message.ProcessedAt.Should().BeNull();
        message.RetryCount.Should().Be(0);

        message.MarkFailed("Network error");
        message.RetryCount.Should().Be(1);
        message.ProcessedAt.Should().BeNull();

        message.MarkFailed("Network error again");
        message.RetryCount.Should().Be(2);
        message.ProcessedAt.Should().BeNull();

        message.MarkDeadLetter("Exceeded max retries");
        message.RetryCount.Should().Be(2);
        message.ProcessedAt.Should().NotBeNull();
        message.Error.Should().StartWith("[DLQ]");
    }

    [Fact]
    public void FullLifecycle_Create_MarkProcessed()
    {
        var message = OutboxMessage.Create("SellerCreated", "{\"name\":\"Loja\"}", DateTime.UtcNow, Guid.NewGuid());

        message.MarkProcessed();

        message.ProcessedAt.Should().NotBeNull();
        message.RetryCount.Should().Be(0);
        message.Error.Should().BeNull();
    }
}
