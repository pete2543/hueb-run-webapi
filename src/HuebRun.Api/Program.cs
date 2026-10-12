using System.Text.Json.Serialization;
using HuebRun.Api;
using HuebRun.Api.Errors;
using HuebRun.Api.Geo;
using HuebRun.Api.Jobs;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Payments;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// โหมดจำลองการชำระเงินให้ "จ่ายผ่าน" ได้โดยไม่มีเงินจริง ห้ามหลุดไปนอกเครื่อง dev
var fakePayments = builder.Configuration.GetValue<string>("Payments:Mode") == PaymentOptions.FakeMode;
if (fakePayments && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException($"Payments:Mode=Fake is only allowed in Development (current: {builder.Environment.EnvironmentName})");

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GeoSearchIndex>();
if (builder.Configuration.GetValue("Jobs:OrderExpiry:Enabled", true))
    builder.Services.AddHostedService<OrderExpiryWorker>();
if (builder.Configuration.GetValue("Jobs:PaymentRecheck:Enabled", true))
    builder.Services.AddHostedService<PaymentRecheckWorker>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ValidationErrorResponse.Create);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>()
    .AddCheck<MigrationsHealthCheck>("migrations");

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (fakePayments)
    app.Logger.LogWarning("Payments:Mode=Fake — slips are NOT verified with SlipOK and are stored on local disk");

app.UseExceptionHandler();
app.UseCors();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapControllers();

await app.SeedDatabaseAsync();
app.Run();

public partial class Program { }
