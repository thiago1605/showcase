namespace FellowCore.Application.Common.Interfaces;

public interface ISecurityService
{
    Task<string> EncryptAsync(string plainText);
    Task<string> DecryptAsync(string encryptedText);
}