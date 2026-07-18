using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Entities;

public class Customer : AggregateRoot<Guid>
{
    [Required]
    public Guid TenantId { get; private set; }
    public virtual Tenant Tenant { get; private set; } = null!;
    public string? ExternalId { get; private set; }

    [Required]
    public string Name { get; private set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; private set; } = string.Empty;
    public string? Document { get; private set; }
    public JsonDocument? Address { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public virtual ICollection<PaymentMethod> PaymentMethods { get; private set; } = new List<PaymentMethod>();
    public virtual ICollection<Transaction> Transactions { get; private set; } = new List<Transaction>();

    protected Customer() { }

    public static Customer Create(Guid tenantId, string name, string email, string? document = null, string? externalId = null)
    {
        return new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Email = email,
            Document = document,
            ExternalId = externalId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Update(string? name, string? email, string? document)
    {
        if (name != null) Name = name;
        if (email != null) Email = email;
        if (document != null) Document = document;
    }

    public PaymentMethod AddPaymentMethod(
        PaymentType type,
        string token,
        PaymentProvider gateway,
        string first6,
        string last4,
        string brand,
        string expiration,
        string holderName,
        string? fingerprint = null,
        bool isDefault = false)
    {
        var paymentMethod = PaymentMethod.Create(Id, type, token, gateway, first6, last4, brand, expiration, holderName, fingerprint, isDefault);
        PaymentMethods.Add(paymentMethod);
        return paymentMethod;
    }
}
