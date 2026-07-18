using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Application.Modules.Auth.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class UserServiceTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly UserService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public UserServiceTests()
    {
        _passwordHasher.Hash(Arg.Any<string>()).Returns("hashed-password");
        _sut = new UserService(_userRepo, _passwordHasher);
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesUser()
    {
        _userRepo.GetByEmailAsync("user@test.com").Returns((User?)null);

        var dto = new CreateUserDto("John Doe", "user@test.com", "SecurePass123!", UserRole.DEVELOPER);
        var result = await _sut.CreateAsync(TenantId, dto);

        result.Should().NotBeNull();
        result.Name.Should().Be("John Doe");
        result.Email.Should().Be("user@test.com");
        result.Role.Should().Be(UserRole.DEVELOPER);
        result.IsActive.Should().BeTrue();
        result.IsTotpEnabled.Should().BeFalse();
        _passwordHasher.Received(1).Hash("SecurePass123!");
        await _userRepo.Received(1).AddAsync(Arg.Any<User>());
    }

    [Fact]
    public async Task CreateAsync_DefaultRole_IsViewer()
    {
        _userRepo.GetByEmailAsync("viewer@test.com").Returns((User?)null);

        var dto = new CreateUserDto("Jane Doe", "viewer@test.com", "SecurePass123!");
        var result = await _sut.CreateAsync(TenantId, dto);

        result.Role.Should().Be(UserRole.VIEWER);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_ThrowsConflictException()
    {
        var existing = User.Create("Existing", "user@test.com", "hash", UserRole.VIEWER, TenantId);
        _userRepo.GetByEmailAsync("user@test.com").Returns(existing);

        var dto = new CreateUserDto("New User", "user@test.com", "password");
        var act = () => _sut.CreateAsync(TenantId, dto);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*email*");
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_ValidUser_DeactivatesUser()
    {
        var user = User.Create("Test User", "user@test.com", "hash", UserRole.VIEWER, TenantId);
        _userRepo.GetByIdAsync(user.Id).Returns(user);

        await _sut.DeleteAsync(TenantId, user.Id);

        user.IsActive.Should().BeFalse();
        await _userRepo.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteAsync_WrongTenant_ThrowsNotFoundException()
    {
        var otherTenantId = Guid.NewGuid();
        var user = User.Create("Test User", "user@test.com", "hash", UserRole.VIEWER, TenantId);
        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.DeleteAsync(otherTenantId, user.Id);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Usuario*");
    }

    [Fact]
    public async Task DeleteAsync_UserNotFound_ThrowsNotFoundException()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((User?)null);

        var act = () => _sut.DeleteAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Usuario*");
    }

    // --- ListAsync ---

    [Fact]
    public async Task ListAsync_ReturnsTenantUsers()
    {
        var users = new List<User>
        {
            User.Create("User 1", "user1@test.com", "hash1", UserRole.VIEWER, TenantId),
            User.Create("User 2", "user2@test.com", "hash2", UserRole.DEVELOPER, TenantId),
            User.Create("User 3", "user3@test.com", "hash3", UserRole.OWNER, TenantId)
        };

        _userRepo.ListByTenantAsync(TenantId).Returns(users);

        var result = await _sut.ListAsync(TenantId);

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("User 1");
        result[1].Role.Should().Be(UserRole.DEVELOPER);
        result[2].Role.Should().Be(UserRole.OWNER);
    }

    [Fact]
    public async Task ListAsync_EmptyTenant_ReturnsEmptyList()
    {
        _userRepo.ListByTenantAsync(TenantId).Returns(new List<User>());

        var result = await _sut.ListAsync(TenantId);

        result.Should().BeEmpty();
    }
}
