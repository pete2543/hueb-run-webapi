using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Errors;

/// แปลง ModelState ที่ไม่ผ่านของ [ApiController] ให้อยู่ในรูปแบบ error กลาง
public static class ValidationErrorResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var details = context.ModelState
            .Where(kv => kv.Value is { Errors.Count: > 0 })
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
        return new BadRequestObjectResult(new ErrorResponse("VALIDATION_ERROR", "ข้อมูลไม่ถูกต้อง", details));
    }
}
