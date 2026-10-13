using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Identity;
using HuebRun.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("DefaultConnection") ?? ""));
        services.AddAdmin(config);
        services.AddScoped<OrderService>();
        services.AddScoped<OrderPaymentService>();
        services.AddScoped<OrderExpiryService>();

        services.AddOptions<PaymentOptions>().Bind(config.GetSection("Payments")).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentRecheckService>();

        if (config.GetValue<string>("Payments:Mode") == PaymentOptions.FakeMode)
        {
            // โหมดจำลอง: ไม่ต้องใช้ key; Program.cs กันไม่ให้เปิดนอก Development
            services.AddOptions<MinioOptions>().Bind(config.GetSection("Minio"));
            services.AddSingleton<ISlipVerifier, DevSlipVerifier>();
            services.AddSingleton<ISlipStorage>(new LocalSlipStorage(Path.Combine(AppContext.BaseDirectory, "App_Data", "slips")));
            return services;
        }

        // ขาด secret แล้วแอปไม่ขึ้น ดีกว่าไปพังตอนนักวิ่งอัปสลิป
        services.AddOptions<SlipOkOptions>().Bind(config.GetSection("SlipOk")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MinioOptions>().Bind(config.GetSection("Minio")).ValidateDataAnnotations().ValidateOnStart();

        services.AddHttpClient<ISlipVerifier, SlipOkVerifier>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<SlipOkOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddSingleton(sp => S3SlipStorage.CreateClient(sp.GetRequiredService<IOptions<MinioOptions>>().Value));
        services.AddSingleton<ISlipStorage, S3SlipStorage>();
        return services;
    }

    private static void AddAdmin(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AdminOptions>().Bind(config.GetSection("Admin")).ValidateDataAnnotations().ValidateOnStart();
        services.AddIdentityCore<AdminUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireDigit = false;
                o.User.RequireUniqueEmail = true;
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddOptions<IdentityOptions>().Configure<IOptions<AdminOptions>>((id, admin) =>
        {
            id.Lockout.MaxFailedAccessAttempts = admin.Value.MaxFailedAttempts;
            id.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(admin.Value.LockoutMinutes);
        });
        services.TryAddScoped<ICurrentAdmin, NoCurrentAdmin>();
        services.AddScoped<AuditWriter>();
        services.AddSingleton<AdminTokenService>();
        services.AddScoped<AdminAuthService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<AdminPaymentService>();
        services.AddScoped<AdminOrderService>();
        services.AddScoped<AdminEventService>();
        services.AddScoped<AdminDashboardService>();
        services.AddScoped<AdminPromoService>();
        services.AddScoped<AdminRunnerService>();
        services.AddScoped<AdminRefundService>();
        services.AddScoped<AdminAuditService>();
        services.AddScoped<AdminCheckInService>();
    }
}
