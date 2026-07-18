using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Pricing.Interfaces;

public interface IProviderCostService
{
    Task<decimal> CalculateProviderCostAsync(PaymentProvider provider, PaymentType paymentType, decimal amount);

    /// <summary>
    /// Calcula o custo total que o provider cobra incluindo o adicional por
    /// parcela (modo "sem juros" — seller absorve). Para <c>installments=1</c>
    /// retorna o mesmo que <see cref="CalculateProviderCostAsync"/>.
    /// </summary>
    Task<decimal> CalculateProviderCostWithInstallmentsAsync(
        PaymentProvider provider,
        PaymentType paymentType,
        decimal amount,
        int installments);
}
