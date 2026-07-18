using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Auth.Interfaces;

public interface IJwtService
{
    string GenerateAccessToken(User user);
    string GenerateMfaPendingToken(Guid userId);
    bool ValidateMfaPendingToken(string token, out Guid userId);
}
