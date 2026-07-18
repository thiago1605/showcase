using System.Net;
using FluentValidation;
using FellowCore.Application.Modules.Webhooks.DTOs;

namespace FellowCore.Application.Modules.Webhooks.Validators;

public class CreateWebhookEndpointDtoValidator : AbstractValidator<CreateWebhookEndpointDto>
{
    public CreateWebhookEndpointDtoValidator()
    {
        // NOTE: No MustAsync here. FluentValidation auto-validation runs synchronously in ASP.NET
        // model binding. DNS resolution is enforced at delivery time via ConnectCallback SSRF guard.
        RuleFor(x => x.Url)
            .NotEmpty().WithMessage("A URL do webhook é obrigatória.")
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            .WithMessage("A URL deve ser HTTPS válida e absoluta.")
            .Must(url => !IsPrivateOrReservedUrl(url))
            .WithMessage("A URL não pode apontar para endereços privados ou reservados.");

        RuleFor(x => x.Secret)
            .NotEmpty().WithMessage("O secret do webhook é obrigatório.")
            .MinimumLength(16).WithMessage("O secret deve ter no mínimo 16 caracteres.");
    }

    private static bool IsPrivateOrReservedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;

        if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(uri.Host, out var ip)) return true;
            return IsPrivateIp(ip);
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1"
            || host.EndsWith(".local")
            || host.EndsWith(".internal")
            || host == "metadata.google.internal"
            || host == "169.254.169.254";
    }

    /// <summary>
    /// Checks if a URL resolves to any private IP. Used by the service layer for
    /// additional registration-time validation (not in the auto-validator).
    /// </summary>
    public static async Task<bool> ResolvesToPrivateIpAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return false; // Already checked by IsPrivateOrReservedUrl

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            // Reject if ANY resolved IP is private (not All — a dual-stack host with one private
            // IP is still a risk). DNS failure is not a rejection (enforce at delivery time).
            return addresses.Length > 0 && addresses.Any(IsPrivateIp);
        }
        catch
        {
            return false; // Can't resolve now — allow registration, enforce at delivery time
        }
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal) return true;

        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        return bytes[0] switch
        {
            0 => true,                                           // 0.0.0.0/8
            10 => true,                                          // 10.0.0.0/8
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,  // 100.64.0.0/10 (CGNAT)
            127 => true,                                         // 127.0.0.0/8
            169 when bytes[1] == 254 => true,                    // 169.254.0.0/16 (link-local + cloud metadata)
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,   // 172.16.0.0/12
            192 when bytes[1] == 168 => true,                    // 192.168.0.0/16
            _ => false
        };
    }
}
