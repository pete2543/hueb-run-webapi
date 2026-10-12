using HuebRun.Infrastructure.Orders;
using HuebRun.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;

namespace HuebRun.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("DefaultConnection") ?? ""));
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
        services.AddSingleton<IMinioClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
            return new MinioClient().WithEndpoint(o.Endpoint).WithCredentials(o.AccessKey, o.SecretKey).WithSSL(o.UseSsl).Build();
        });
        services.AddSingleton<ISlipStorage, MinioSlipStorage>();
        return services;
    }
}
