namespace FellowCore.Api.Filters;

[AttributeUsage(AttributeTargets.Method)]
public class AuditActionAttribute(string action) : Attribute
{
    public string Action { get; } = action;
}
