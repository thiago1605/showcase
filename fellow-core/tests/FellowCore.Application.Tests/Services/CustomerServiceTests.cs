using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Customers.DTOs;
using FellowCore.Application.Modules.Customers.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class CustomerServiceTests
{
    private readonly ICustomerRepository _customerRepository = Substitute.For<ICustomerRepository>();
    private readonly CustomerService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public CustomerServiceTests()
    {
        _sut = new CustomerService(_customerRepository);
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesCustomer()
    {
        _customerRepository.GetByEmailAsync(TenantId, "customer@test.com").Returns((Customer?)null);

        var dto = new CreateCustomerDto("John Doe", "customer@test.com", "12345678901", "ext-001");
        var result = await _sut.CreateAsync(TenantId, dto);

        result.Should().NotBeNull();
        result.Name.Should().Be("John Doe");
        result.Email.Should().Be("customer@test.com");
        result.Document.Should().Be("12345678901");
        result.ExternalId.Should().Be("ext-001");
        _customerRepository.Received(1).Add(Arg.Any<Customer>());
        await _customerRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_ThrowsConflictException()
    {
        var existing = Customer.Create(TenantId, "Existing", "customer@test.com");
        _customerRepository.GetByEmailAsync(TenantId, "customer@test.com").Returns(existing);

        var dto = new CreateCustomerDto("New Customer", "customer@test.com");
        var act = () => _sut.CreateAsync(TenantId, dto);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*email*");
    }

    [Fact]
    public async Task CreateAsync_WithOptionalFields_CreatesWithDefaults()
    {
        _customerRepository.GetByEmailAsync(TenantId, "minimal@test.com").Returns((Customer?)null);

        var dto = new CreateCustomerDto("Minimal Customer", "minimal@test.com");
        var result = await _sut.CreateAsync(TenantId, dto);

        result.Should().NotBeNull();
        result.Document.Should().BeNull();
        result.ExternalId.Should().BeNull();
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_ExistingCustomer_ReturnsDetail()
    {
        var customer = Customer.Create(TenantId, "Test Customer", "test@test.com", "99988877766");
        _customerRepository.GetByIdAsync(TenantId, customer.Id).Returns(customer);

        var result = await _sut.GetByIdAsync(TenantId, customer.Id);

        result.Should().NotBeNull();
        result.Name.Should().Be("Test Customer");
        result.Email.Should().Be("test@test.com");
        result.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ThrowsNotFoundException()
    {
        _customerRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Customer?)null);

        var act = () => _sut.GetByIdAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Cliente*");
    }

    // --- ListAsync ---

    [Fact]
    public async Task ListAsync_ReturnsPagedResult()
    {
        var customers = new List<Customer>
        {
            Customer.Create(TenantId, "Customer 1", "c1@test.com"),
            Customer.Create(TenantId, "Customer 2", "c2@test.com")
        };

        _customerRepository.GetPagedAsync(TenantId, 0, 20)
            .Returns((customers.AsReadOnly(), 2));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task ListAsync_EmptyResult_ReturnsEmptyPage()
    {
        _customerRepository.GetPagedAsync(TenantId, 0, 20)
            .Returns((new List<Customer>().AsReadOnly(), 0));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // --- UpdateAsync ---

    [Fact]
    public async Task UpdateAsync_ValidInput_UpdatesCustomer()
    {
        var customer = Customer.Create(TenantId, "Old Name", "old@test.com");
        _customerRepository.GetByIdAsync(TenantId, customer.Id).Returns(customer);

        var dto = new UpdateCustomerDto(Name: "New Name", Email: "new@test.com");
        var result = await _sut.UpdateAsync(TenantId, customer.Id, dto);

        result.Name.Should().Be("New Name");
        result.Email.Should().Be("new@test.com");
        _customerRepository.Received(1).Update(customer);
        await _customerRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateAsync_DuplicateEmail_ThrowsConflictException()
    {
        var customer = Customer.Create(TenantId, "Customer A", "a@test.com");
        var otherCustomer = Customer.Create(TenantId, "Customer B", "b@test.com");

        _customerRepository.GetByIdAsync(TenantId, customer.Id).Returns(customer);
        _customerRepository.GetByEmailAsync(TenantId, "b@test.com").Returns(otherCustomer);

        var dto = new UpdateCustomerDto(Email: "b@test.com");
        var act = () => _sut.UpdateAsync(TenantId, customer.Id, dto);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*email*");
    }

    [Fact]
    public async Task UpdateAsync_SameEmail_NoConflict()
    {
        var customer = Customer.Create(TenantId, "Customer A", "same@test.com");
        _customerRepository.GetByIdAsync(TenantId, customer.Id).Returns(customer);
        _customerRepository.GetByEmailAsync(TenantId, "same@test.com").Returns(customer);

        var dto = new UpdateCustomerDto(Email: "same@test.com", Name: "Updated Name");
        var result = await _sut.UpdateAsync(TenantId, customer.Id, dto);

        // Should not throw — same customer email
        result.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateAsync_NotFound_ThrowsNotFoundException()
    {
        _customerRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Customer?)null);

        var dto = new UpdateCustomerDto(Name: "New Name");
        var act = () => _sut.UpdateAsync(TenantId, Guid.NewGuid(), dto);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Cliente*");
    }
}
