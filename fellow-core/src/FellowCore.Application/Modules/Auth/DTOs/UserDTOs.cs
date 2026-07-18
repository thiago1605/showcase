using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Auth.DTOs;

public record CreateUserDto(string Name, string Email, string Password, UserRole Role = UserRole.VIEWER, Guid? SellerId = null);

public record UserResponse(
    Guid Id,
    string Name,
    string Email,
    UserRole Role,
    bool IsTotpEnabled,
    bool IsActive,
    DateTime? LastLogin,
    DateTime CreatedAt,
    Guid? SellerId
);
