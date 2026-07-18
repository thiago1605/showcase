namespace FellowCore.Api.Responses;

public class ApiResponse
{
    public bool Success { get; set; } = true;
    public string? Message { get; set; } = null;
    public object? Data { get; set; } = null;
    public List<string>? Errors { get; set; } = null;

    public static ApiResponse Ok(object? data, string? message = null)
    {
        return new () { Success = true, Data = data, Message = message };
    }

    public static ApiResponse Fail(string error, string? message = null)
    {
        return new () { Success = false, Errors = [error], Message = message };
    }
}