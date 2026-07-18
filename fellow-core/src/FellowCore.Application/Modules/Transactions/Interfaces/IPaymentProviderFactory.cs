

using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Transactions.Interfaces;

public interface IPaymentProviderFactory
{
    IPaymentProvider GetProvider(PaymentProvider providerType);
}