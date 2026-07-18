using FluentAssertions;
using FellowCore.Domain.ValueObjects;

namespace FellowCore.Domain.Tests.ValueObjects;

public class DocumentTests
{
    // CPF válido: 529.982.247-25
    private const string ValidCpf = "52998224725";
    // CNPJ válido: 11.222.333/0001-81
    private const string ValidCnpj = "11222333000181";

    [Fact]
    public void Create_ShouldSucceed_WithValidCpf()
    {
        var result = Document.Create(ValidCpf);

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be(DocumentType.CPF);
        result.Value.Value.Should().Be(ValidCpf);
    }

    [Fact]
    public void Create_ShouldSucceed_WithFormattedCpf()
    {
        var result = Document.Create("529.982.247-25");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(ValidCpf);
    }

    [Theory]
    [InlineData("11111111111")]   // todos dígitos iguais
    [InlineData("00000000000")]
    [InlineData("12345678900")]   // dígito verificador inválido
    public void Create_ShouldFail_WithInvalidCpf(string cpf)
    {
        var result = Document.Create(cpf);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Document.InvalidCpf");
    }

    [Fact]
    public void Create_ShouldSucceed_WithValidCnpj()
    {
        var result = Document.Create(ValidCnpj);

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be(DocumentType.CNPJ);
        result.Value.Value.Should().Be(ValidCnpj);
    }

    [Fact]
    public void Create_ShouldSucceed_WithFormattedCnpj()
    {
        var result = Document.Create("11.222.333/0001-81");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(ValidCnpj);
    }

    [Theory]
    [InlineData("11111111111111")]  // todos dígitos iguais
    [InlineData("12345678000100")]  // dígito verificador inválido
    public void Create_ShouldFail_WithInvalidCnpj(string cnpj)
    {
        var result = Document.Create(cnpj);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Document.InvalidCnpj");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldFail_WhenEmpty(string? value)
    {
        var result = Document.Create(value);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Document.Empty");
    }

    [Fact]
    public void Create_ShouldFail_WhenLengthIsInvalid()
    {
        var result = Document.Create("123456");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Document.InvalidLength");
    }

    [Fact]
    public void Equality_ShouldBeValueBased()
    {
        var a = Document.Create(ValidCpf).Value;
        var b = Document.Create(ValidCpf).Value;

        a.Should().Be(b);
    }
}
