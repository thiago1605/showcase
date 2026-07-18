using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Tenants.DTOs;
using FellowCore.Application.Modules.Tenants.Validators;

namespace FellowCore.Application.Tests.Validators;

public class CreateTenantDtoValidatorTests
{
    private readonly CreateTenantDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(new CreateTenantDto { Name = "Meu Tenant", Slug = "meu-tenant" })
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Name_Empty_ShouldFail() =>
        _validator.TestValidate(new CreateTenantDto { Name = "", Slug = "slug" })
            .ShouldHaveValidationErrorFor(x => x.Name);

    [Fact]
    public void Slug_Empty_ShouldFail() =>
        _validator.TestValidate(new CreateTenantDto { Name = "Tenant", Slug = "" })
            .ShouldHaveValidationErrorFor(x => x.Slug);

    [Theory]
    [InlineData("UPPER")]
    [InlineData("with spaces")]
    [InlineData("special@chars")]
    [InlineData("-leading-hyphen")]
    public void Slug_InvalidFormat_ShouldFail(string slug) =>
        _validator.TestValidate(new CreateTenantDto { Name = "Tenant", Slug = slug })
            .ShouldHaveValidationErrorFor(x => x.Slug);

    [Theory]
    [InlineData("valid-slug")]
    [InlineData("slug123")]
    [InlineData("my-app-v2")]
    public void Slug_ValidFormat_ShouldPass(string slug) =>
        _validator.TestValidate(new CreateTenantDto { Name = "Tenant", Slug = slug })
            .ShouldNotHaveValidationErrorFor(x => x.Slug);
}
