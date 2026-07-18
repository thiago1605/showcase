using System.Security.Cryptography;
using System.Text;

namespace FellowCore.Application.Common.Utils;

public static class CryptoUtils
{
    /// <summary>
    /// Gera uma string Hexadecimal aleatória (segura para criptografia).
    /// </summary>
    public static string GenerateRandomHex(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToHexString(bytes).ToLower();
    }

    /// <summary>
    /// Gera o Hash SHA256 de uma string (Para salvar senhas e secrets no banco).
    /// </summary>
    public static string GenerateSha256Hash(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLower();
    }

    /// <summary>
    /// Gera a assinatura HMAC-SHA256 para payloads de Webhook.
    /// </summary>
    public static string GenerateHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        
        var hashBytes = HMACSHA256.HashData(keyBytes, payloadBytes);
        
        return Convert.ToHexString(hashBytes).ToLower();
    }
}