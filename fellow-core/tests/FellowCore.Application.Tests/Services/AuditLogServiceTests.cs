using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.AuditLogs.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class AuditLogServiceTests
{
    private readonly IAuditLogRepository _repository = Substitute.For<IAuditLogRepository>();
    private readonly AuditLogService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public AuditLogServiceTests()
    {
        _sut = new AuditLogService(_repository);
    }

    #region LogAsync

    [Fact]
    public async Task LogAsync_ShouldPersistAuditLog()
    {
        await _sut.LogAsync(TenantId, "POST /api/transactions", "tx-123", "192.168.1.1", "corr-001", 201);

        await _repository.Received(1).AddAsync(Arg.Is<AuditLog>(log =>
            log.TenantId == TenantId &&
            log.Action == "POST /api/transactions" &&
            log.ResourceId == "tx-123" &&
            log.IpAddress == "192.168.1.1" &&
            log.CorrelationId == "corr-001" &&
            log.StatusCode == 201));
    }

    [Fact]
    public async Task LogAsync_ShouldAcceptNullOptionalFields()
    {
        await _sut.LogAsync(TenantId, "GET /api/sellers", null, null, null, 200);

        await _repository.Received(1).AddAsync(Arg.Is<AuditLog>(log =>
            log.TenantId == TenantId &&
            log.Action == "GET /api/sellers" &&
            log.ResourceId == null &&
            log.IpAddress == null &&
            log.CorrelationId == null &&
            log.StatusCode == 200));
    }

    [Fact]
    public async Task LogAsync_ShouldGenerateUniqueId()
    {
        AuditLog? capturedLog = null;
        await _repository.AddAsync(Arg.Do<AuditLog>(log => capturedLog = log));

        await _sut.LogAsync(TenantId, "DELETE /api/sellers/1", "seller-1", "10.0.0.1", "corr-002", 204);

        capturedLog.Should().NotBeNull();
        capturedLog!.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task LogAsync_ShouldSetCreatedAtToNow()
    {
        AuditLog? capturedLog = null;
        await _repository.AddAsync(Arg.Do<AuditLog>(log => capturedLog = log));

        var before = DateTime.UtcNow;
        await _sut.LogAsync(TenantId, "PUT /api/tenants", "t-1", null, null, 200);
        var after = DateTime.UtcNow;

        capturedLog.Should().NotBeNull();
        capturedLog!.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    #endregion

    #region ListAsync

    [Fact]
    public async Task ListAsync_ShouldReturnPagedResult()
    {
        var log1 = AuditLog.Create(TenantId, "GET /api/tx", null, "1.2.3.4", "c1", 200);
        var log2 = AuditLog.Create(TenantId, "POST /api/tx", "tx-1", "1.2.3.4", "c2", 201);

        _repository.ListByTenantAsync(TenantId, null, 0, 20)
            .Returns((new List<AuditLog> { log1, log2 }.AsReadOnly(), 2));

        var result = await _sut.ListAsync(TenantId, null, 1, 20);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByAction()
    {
        var log1 = AuditLog.Create(TenantId, "POST /api/transactions", "tx-1", null, null, 201);

        _repository.ListByTenantAsync(TenantId, "POST /api/transactions", 0, 20)
            .Returns((new List<AuditLog> { log1 }.AsReadOnly(), 1));

        var result = await _sut.ListAsync(TenantId, "POST /api/transactions", 1, 20);

        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be("POST /api/transactions");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmpty_WhenNoLogs()
    {
        _repository.ListByTenantAsync(TenantId, null, 0, 20)
            .Returns((new List<AuditLog>().AsReadOnly(), 0));

        var result = await _sut.ListAsync(TenantId, null, 1, 20);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldNormalizePagination()
    {
        _repository.ListByTenantAsync(TenantId, null, Arg.Any<int>(), Arg.Any<int>())
            .Returns((new List<AuditLog>().AsReadOnly(), 0));

        // Page 0 -> normalized to 1, pageSize 200 -> clamped to 100
        var result = await _sut.ListAsync(TenantId, null, 0, 200);

        result.Page.Should().Be(1);
        result.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task ListAsync_ShouldMapAllFieldsCorrectly()
    {
        var log = AuditLog.Create(TenantId, "PATCH /api/sellers/1", "s-1", "10.10.10.10", "corr-x", 200);

        _repository.ListByTenantAsync(TenantId, null, 0, 20)
            .Returns((new List<AuditLog> { log }.AsReadOnly(), 1));

        var result = await _sut.ListAsync(TenantId, null, 1, 20);

        var dto = result.Items[0];
        dto.Id.Should().Be(log.Id);
        dto.TenantId.Should().Be(TenantId);
        dto.Action.Should().Be("PATCH /api/sellers/1");
        dto.ResourceId.Should().Be("s-1");
        dto.IpAddress.Should().Be("10.10.10.10");
        dto.CorrelationId.Should().Be("corr-x");
        dto.StatusCode.Should().Be(200);
    }

    #endregion

    #region Tenant Isolation

    [Fact]
    public async Task ListAsync_ShouldUseTenantId_ForIsolation()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        _repository.ListByTenantAsync(tenantA, null, 0, 20)
            .Returns((new List<AuditLog>().AsReadOnly(), 0));

        await _sut.ListAsync(tenantA, null, 1, 20);

        await _repository.Received(1).ListByTenantAsync(tenantA, null, 0, 20);
        await _repository.DidNotReceive().ListByTenantAsync(tenantB, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task LogAsync_ShouldAssociateTenantId()
    {
        var tenantA = Guid.NewGuid();

        await _sut.LogAsync(tenantA, "GET /health", null, null, null, 200);

        await _repository.Received(1).AddAsync(Arg.Is<AuditLog>(log => log.TenantId == tenantA));
    }

    #endregion
}
