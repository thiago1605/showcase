using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Settlements.AdvanceRisk;

/// <summary>
/// Avalia se uma TX é elegível pra antecipação automática (modo ADVANCE).
/// Chamado pelo capture hook ANTES de marcar a TX como ADVANCE.
///
/// Filosofia: regras conservadoras por padrão. Antecipação = plataforma adianta
/// caixa próprio, qualquer fraude/chargeback vira prejuízo direto. Throttle silencioso
/// (fallback INSTALLMENT) preserva UX do seller — ele recebe parcelado em vez de
/// receber erro 422.
///
/// Regras MVP cobertas (R1-R5):
///   R1: Seller ACTIVE + KYC completo (ExternalAccountId não-nulo)
///   R2: Idade mínima do seller (default 30 dias)
///   R3: Cap de valor pra seller novo (< 90 dias) — default R$ 5.000
///   R4: RiskScore da TX abaixo de threshold (default 0.7)
///   R5: Chargeback rate histórico < threshold (default 1%)
///
/// Não cobre (roadmap):
///   - Velocity per cartão / per CPF cross-seller
///   - Device fingerprint / geo
///   - Bin lookup (pré-pago vs débito virtual)
/// </summary>
public interface IAdvanceRiskEvaluator
{
    Task<AdvanceRiskEvaluation> EvaluateAsync(Transaction tx, Seller seller, CancellationToken ct = default);
}

/// <summary>
/// Resultado da avaliação. Quando <see cref="IsEligible"/> = false, capture hook
/// faz fallback INSTALLMENT silencioso e emite métrica <c>RecordAdvanceThrottled(BlockReason)</c>.
/// </summary>
/// <param name="IsEligible">true → pode antecipar; false → fallback INSTALLMENT</param>
/// <param name="BlockReason">Código curto pra telemetria (e.g. "seller_too_new"). Null quando IsEligible=true.</param>
/// <param name="Signals">Diagnóstico legível humano (e.g. "score=0.85", "cb_rate=2.3%"). Pode ser vazio.</param>
public record AdvanceRiskEvaluation(bool IsEligible, string? BlockReason, IReadOnlyList<string> Signals)
{
    public static AdvanceRiskEvaluation Eligible() => new(true, null, []);
    public static AdvanceRiskEvaluation Block(string reason, params string[] signals) => new(false, reason, signals);
}
