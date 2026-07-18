using FluentValidation;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Auth.Validators;

public class CreateUserDtoValidator : AbstractValidator<CreateUserDto>
{
    public CreateUserDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .NotEmpty().EmailAddress().MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12).WithMessage("A senha deve ter no mínimo 12 caracteres.")
            .MaximumLength(128)
            .Matches(@"[A-Z]").WithMessage("A senha deve conter pelo menos uma letra maiúscula.")
            .Matches(@"[a-z]").WithMessage("A senha deve conter pelo menos uma letra minúscula.")
            .Matches(@"\d").WithMessage("A senha deve conter pelo menos um número.")
            .Matches(@"[^a-zA-Z\d]").WithMessage("A senha deve conter pelo menos um caractere especial.");

        RuleFor(x => x.Role).IsInEnum().WithMessage("Role inválida.");
    }
}

public class LoginDtoValidator : AbstractValidator<LoginDto>
{
    public LoginDtoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public class VerifyMfaDtoValidator : AbstractValidator<VerifyMfaDto>
{
    public VerifyMfaDtoValidator()
    {
        RuleFor(x => x.MfaToken).NotEmpty();
        RuleFor(x => x.TotpCode).NotEmpty()
            .Matches(@"^\d{6,8}$").WithMessage("O código deve ter 6 a 8 dígitos.");
    }
}

public class EnableTotpDtoValidator : AbstractValidator<EnableTotpDto>
{
    public EnableTotpDtoValidator()
    {
        RuleFor(x => x.TotpCode).NotEmpty()
            .Matches(@"^\d{6}$").WithMessage("O código TOTP deve ter exatamente 6 dígitos.");
    }
}

public class DisableTotpDtoValidator : AbstractValidator<DisableTotpDto>
{
    public DisableTotpDtoValidator()
    {
        RuleFor(x => x.TotpCode).NotEmpty()
            .Matches(@"^\d{6}$").WithMessage("O código TOTP deve ter exatamente 6 dígitos.");
    }
}

public class ForgotPasswordDtoValidator : AbstractValidator<ForgotPasswordDto>
{
    public ForgotPasswordDtoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public class ResetPasswordDtoValidator : AbstractValidator<ResetPasswordDto>
{
    public ResetPasswordDtoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
        RuleFor(x => x.NewPassword).NotEmpty()
            .MinimumLength(12).WithMessage("A senha deve ter no mínimo 12 caracteres.")
            .MaximumLength(128)
            .Matches(@"[A-Z]").WithMessage("A senha deve conter pelo menos uma letra maiúscula.")
            .Matches(@"[a-z]").WithMessage("A senha deve conter pelo menos uma letra minúscula.")
            .Matches(@"\d").WithMessage("A senha deve conter pelo menos um número.")
            .Matches(@"[^a-zA-Z\d]").WithMessage("A senha deve conter pelo menos um caractere especial.");
    }
}

public class RefreshTokenRequestDtoValidator : AbstractValidator<RefreshTokenRequestDto>
{
    public RefreshTokenRequestDtoValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}
