using HuebRun.Domain;
using Microsoft.AspNetCore.Diagnostics;

namespace HuebRun.Api.Errors;

public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        int status;
        ErrorResponse body;
        switch (exception)
        {
            case AppException app:
                status = app.StatusCode;
                body = new ErrorResponse(app.Code, app.Message, app.Details);
                break;
            default:
                logger.LogError(exception, "Unhandled exception");
                status = StatusCodes.Status500InternalServerError;
                body = new ErrorResponse("INTERNAL_ERROR", "เกิดข้อผิดพลาดภายในระบบ", null);
                break;
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body, ct);
        return true;
    }
}
