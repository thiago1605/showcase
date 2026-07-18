using FellowCore.Application.Modules.Auth.DTOs;

namespace FellowCore.Application.Modules.Auth.Interfaces;

public interface IUserService
{
    Task<UserResponse> CreateAsync(Guid tenantId, CreateUserDto dto);
    Task<List<UserResponse>> ListAsync(Guid tenantId);
    Task<UserResponse?> GetByIdAsync(Guid tenantId, Guid userId);
    Task DeleteAsync(Guid tenantId, Guid userId);
}
