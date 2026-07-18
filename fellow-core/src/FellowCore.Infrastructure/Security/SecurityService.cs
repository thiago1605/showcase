using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FellowCore.Infrastructure.Security;

public class SecurityService(IOptions<SecurityOptions> options, ILogger<SecurityService> logger) : ISecurityService
{
    // AES-GCM: 12-byte nonce + 16-byte tag + ciphertext, prefixed with version byte 0x02
    private const byte VersionGcm = 0x02;
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // 128-bit authentication tag

    private byte[] GetMasterKey()
    {
        string key = options.Value.MasterKey;

        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("MasterKey de segurança não configurada no appsettings.");

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length < 16)
            throw new InvalidOperationException("MasterKey deve ter pelo menos 16 caracteres.");

        return SHA256.HashData(keyBytes);
    }

    public Task<string> EncryptAsync(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return Task.FromResult(string.Empty);

        var key = GetMasterKey();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Format: [version(1)] [nonce(12)] [tag(16)] [ciphertext(N)]
        var result = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        result[0] = VersionGcm;
        Buffer.BlockCopy(nonce, 0, result, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, result, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, result, 1 + NonceSize + TagSize, cipherBytes.Length);

        return Task.FromResult(Convert.ToBase64String(result));
    }

    public Task<string> DecryptAsync(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText)) return Task.FromResult(string.Empty);

        var fullCipher = Convert.FromBase64String(encryptedText);

        // Legacy AES-CBC: no version prefix, starts with 16-byte IV directly (total >= 32 bytes)
        if (fullCipher.Length >= 32 && fullCipher[0] != VersionGcm)
            return DecryptLegacyCbcAsync(fullCipher);

        // AES-GCM format: [version(1)] [nonce(12)] [tag(16)] [ciphertext(N)]
        if (fullCipher.Length < 1 + NonceSize + TagSize)
            throw new CryptographicException("Encrypted data is too short.");

        if (fullCipher[0] != VersionGcm)
            throw new CryptographicException($"Unknown encryption version: {fullCipher[0]}");

        var key = GetMasterKey();
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherBytes = new byte[fullCipher.Length - 1 - NonceSize - TagSize];

        Buffer.BlockCopy(fullCipher, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(fullCipher, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(fullCipher, 1 + NonceSize + TagSize, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Task.FromResult(Encoding.UTF8.GetString(plainBytes));
    }

    /// <summary>
    /// Backward-compatible decryption for data encrypted with the old AES-CBC (no HMAC) format.
    /// Format: [IV(16)] [ciphertext(N)]
    /// </summary>
    private Task<string> DecryptLegacyCbcAsync(byte[] fullCipher)
    {
        logger.LogWarning("Decrypting legacy AES-CBC ciphertext. Consider re-encrypting with AES-GCM.");

        using var aes = Aes.Create();
        aes.Key = GetMasterKey();

        var iv = new byte[16];
        var cipherBytes = new byte[fullCipher.Length - 16];
        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullCipher, 16, cipherBytes, 0, cipherBytes.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Task.FromResult(Encoding.UTF8.GetString(plainBytes));
    }
}