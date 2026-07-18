using FluentAssertions;
using FellowCore.Domain.ValueObjects;

namespace FellowCore.Domain.Tests.ValueObjects;

public class EmailTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("USER@EXAMPLE.COM")]
    [InlineData("user.name+tag@sub.domain.com")]
    public void Create_ShouldSucceed_WithValidEmail(string email)
    {
        var result = Email.Create(email);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(email.ToLowerInvariant());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("@no-local.com")]
    [InlineData("spaces in@email.com")]
    public void Create_ShouldFail_WithInvalidFormat(string email)
    {
        var result = Email.Create(email);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Email.InvalidFormat");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldFail_WhenEmpty(string? email)
    {
        var result = Email.Create(email);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Email.Empty");
    }

    [Fact]
    public void Equality_ShouldBeValueBased_AndCaseInsensitive()
    {
        var a = Email.Create("User@Example.COM").Value;
        var b = Email.Create("user@example.com").Value;

        a.Should().Be(b);
    }
}
