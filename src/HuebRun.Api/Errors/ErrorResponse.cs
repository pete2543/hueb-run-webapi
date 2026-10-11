namespace HuebRun.Api.Errors;

public sealed record ErrorResponse(string Code, string Message, object? Details);
