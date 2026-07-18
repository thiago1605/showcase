using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Auth.Services;

public class UserService(IUserRepository userRepo, IPasswordHasher passwordHasher) : IUserService
{
    public async Task<UserResponse> CreateAsync(Guid tenantId, CreateUserDto dto)
    {
        var existing = await userRepo.GetByEmailAsync(dto.Email);
        if (existing != null)
            throw new ConflictException("User.DuplicateEmail", "Ja existe um usuario com esse email.");

        var passwordHash = passwordHasher.Hash(dto.Password);
        var user = User.Create(dto.Name, dto.Email, passwordHash, dto.Role, tenantId, dto.SellerId);
        await userRepo.AddAsync(user);
        return ToResponse(user);
    }

    public async Task<List<UserResponse>> ListAsync(Guid tenantId)
    {
        var users = await userRepo.ListByTenantAsync(tenantId);
        return users.Select(ToResponse).ToList();
    }

    public async Task<UserResponse?> GetByIdAsync(Guid tenantId, Guid userId)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user == null || user.TenantId != tenantId) return null;
        return ToResponse(user);
    }

    public async Task DeleteAsync(Guid tenantId, Guid userId)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        if (user.TenantId != tenantId)
            throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        user.Deactivate();
        await userRepo.SaveChangesAsync();
    }

    private static UserResponse ToResponse(User u) =>
        new(u.Id, u.Name, u.Email, u.Role, u.IsTotpEnabled, u.IsActive, u.LastLogin, u.CreatedAt, u.SellerId);
}
