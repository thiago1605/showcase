using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.ValueObjects;

public sealed class FeeSchedule : ValueObject
{
    public decimal FeeDebit { get; private set; }
    public decimal FeeCreditCash { get; private set; }
    public decimal FeeCreditInstallment { get; private set; }
    public decimal FeePixIn { get; private set; }

    private FeeSchedule() { } // EF Core

    private FeeSchedule(decimal feeDebit, decimal feeCreditCash, decimal feeCreditInstallment, decimal feePixIn)
    {
        FeeDebit = feeDebit;
        FeeCreditCash = feeCreditCash;
        FeeCreditInstallment = feeCreditInstallment;
        FeePixIn = feePixIn;
    }

    public static readonly FeeSchedule Default = new(2.00m, 4.50m, 6.50m, 1.50m);

    public static Result<FeeSchedule> Create(decimal feeDebit, decimal feeCreditCash, decimal feeCreditInstallment, decimal feePixIn)
    {
        if (feeDebit < 0 || feeDebit > 100)
            return Result.Failure<FeeSchedule>(Error.Validation("FeeSchedule.InvalidFeeDebit", "A taxa de débito deve estar entre 0 e 100."));

        if (feeCreditCash < 0 || feeCreditCash > 100)
            return Result.Failure<FeeSchedule>(Error.Validation("FeeSchedule.InvalidFeeCreditCash", "A taxa de crédito à vista deve estar entre 0 e 100."));

        if (feeCreditInstallment < 0 || feeCreditInstallment > 100)
            return Result.Failure<FeeSchedule>(Error.Validation("FeeSchedule.InvalidFeeCreditInstallment", "A taxa de crédito parcelado deve estar entre 0 e 100."));

        if (feePixIn < 0 || feePixIn > 100)
            return Result.Failure<FeeSchedule>(Error.Validation("FeeSchedule.InvalidFeePixIn", "A taxa de Pix deve estar entre 0 e 100."));

        return Result.Success(new FeeSchedule(feeDebit, feeCreditCash, feeCreditInstallment, feePixIn));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return FeeDebit;
        yield return FeeCreditCash;
        yield return FeeCreditInstallment;
        yield return FeePixIn;
    }
}
