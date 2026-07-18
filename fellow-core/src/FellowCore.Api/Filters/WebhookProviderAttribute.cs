using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace FellowCore.Api.Filters;

[AttributeUsage(AttributeTargets.Method)]
public class WebhookProviderAttribute : TypeFilterAttribute
{
    public WebhookProviderAttribute(PaymentProvider provider) : base(typeof(WebhookAuthFilter)) 
        => Arguments = [provider];
    
}