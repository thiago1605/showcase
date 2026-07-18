using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Validators;

namespace FellowCore.Application.Tests.Validators;

public class CreateWebhookEndpointDtoValidatorTests
{
    private readonly CreateWebhookEndpointDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass()
    {
        var result = _validator.TestValidate(new CreateWebhookEndpointDto("https://example.com/hook", "secret1234567890ab"));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Url_Empty_ShouldFail()
    {
        var result = _validator.TestValidate(new CreateWebhookEndpointDto("", "secret1234567890ab"));
        result.ShouldHaveValidationErrorFor(x => x.Url);
    }

    [Fact]
    public void Url_Invalid_ShouldFail()
    {
        var result = _validator.TestValidate(new CreateWebhookEndpointDto("not-a-url", "secret1234567890ab"));
        result.ShouldHaveValidationErrorFor(x => x.Url);
    }

    [Fact]
    public void Secret_TooShort_ShouldFail()
    {
        var result = _validator.TestValidate(new CreateWebhookEndpointDto("https://example.com/hook", "short"));
        result.ShouldHaveValidationErrorFor(x => x.Secret);
    }

    [Fact]
    public async Task ResolvesToPrivateIp_Localhost_ShouldBlock()
    {
        var result = await CreateWebhookEndpointDtoValidator.ResolvesToPrivateIpAsync("https://localhost/hook");
        // localhost resolves to 127.0.0.1 (loopback) which is private — should be blocked
        Assert.True(result);
    }
}
