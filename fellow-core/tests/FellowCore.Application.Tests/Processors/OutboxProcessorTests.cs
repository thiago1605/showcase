using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Primitives;
using FellowCore.Infrastructure.Database;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Processors;

public class OutboxProcessorTests
{
    private readonly IDomainEventDispatcher _dispatcher = Substitute.For<IDomainEventDispatcher>();
    private readonly ILogger<OutboxProcessor> _logger = Substitute.For<ILogger<OutboxProcessor>>();

    private AppDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"OutboxTest_{Guid.NewGuid()}")
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDispatchEvents_WhenPendingMessagesExist()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var eventType = typeof(TestDomainEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(new TestDomainEvent(DateTime.UtcNow));

        var message = OutboxMessage.Create(eventType, payload, DateTime.UtcNow);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<IReadOnlyList<IDomainEvent>>(events => events.Count == 1),
            Arg.Any<CancellationToken>());

        var processed = await context.OutboxMessages.FirstAsync();
        processed.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_ShouldDoNothing_WhenNoMessagesExist()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<IReadOnlyList<IDomainEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldMarkFailed_WhenEventTypeNotFound()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var message = OutboxMessage.Create("NonExistent.Type, NonExistent.Assembly", "{}", DateTime.UtcNow);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<IReadOnlyList<IDomainEvent>>(),
            Arg.Any<CancellationToken>());

        var updated = await context.OutboxMessages.FirstAsync();
        updated.ProcessedAt.Should().BeNull();
        updated.RetryCount.Should().Be(1);
        updated.Error.Should().Contain("Type not found");
    }

    [Fact]
    public async Task ProcessAsync_ShouldMarkFailed_WhenDeserializationFails()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var eventType = typeof(TestDomainEvent).AssemblyQualifiedName!;
        // Invalid JSON that won't deserialize to TestDomainEvent properly
        var message = OutboxMessage.Create(eventType, "not-valid-json", DateTime.UtcNow);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        var updated = await context.OutboxMessages.FirstAsync();
        // Either the deserialization throws (MarkFailed via catch) or returns null (MarkFailed via null check)
        updated.RetryCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ProcessAsync_ShouldMarkFailed_WhenDispatcherThrows()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var eventType = typeof(TestDomainEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(new TestDomainEvent(DateTime.UtcNow));
        var message = OutboxMessage.Create(eventType, payload, DateTime.UtcNow);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        _dispatcher.DispatchAsync(Arg.Any<IReadOnlyList<IDomainEvent>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Dispatch failed"));

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        var updated = await context.OutboxMessages.FirstAsync();
        updated.ProcessedAt.Should().BeNull();
        updated.RetryCount.Should().Be(1);
        updated.Error.Should().Contain("Dispatch failed");
    }

    [Fact]
    public async Task ProcessAsync_ShouldSkipMessages_WithRetryCountAtMax()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var message = OutboxMessage.Create("SomeType", "{}", DateTime.UtcNow);
        // Increment retry count to 5 (MaxRetries)
        for (int i = 0; i < 5; i++)
            message.MarkFailed("error");

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<IReadOnlyList<IDomainEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldSkipAlreadyProcessedMessages()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var eventType = typeof(TestDomainEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(new TestDomainEvent(DateTime.UtcNow));
        var message = OutboxMessage.Create(eventType, payload, DateTime.UtcNow);
        message.MarkProcessed();
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();

        var sut = new OutboxProcessor(context, _dispatcher, _logger);

        // Act
        await sut.ProcessAsync();

        // Assert
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<IReadOnlyList<IDomainEvent>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A simple domain event used for testing serialization/deserialization.
    /// </summary>
    public record TestDomainEvent(DateTime OccurredAt) : IDomainEvent;
}
