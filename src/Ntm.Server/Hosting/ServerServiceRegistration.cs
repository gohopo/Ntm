using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ntm.Server.Admin;

namespace Ntm.Server.Hosting;

internal static class ServerServiceRegistration
{
    /// <summary>管理接口要的东西：错误格式、JSON 约定、管理员会话和登录限流。</summary>
    public static IServiceCollection AddNtmServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MetricsOptions>(configuration.GetSection(MetricsOptions.SectionName));
        services.AddProblemDetails();

        // 绑定失败在开发和生产环境都抛出来，由 BadRequestExceptionHandler 回带字段名的 400（Q304）。
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<BadRequestExceptionHandler>();

        services.ConfigureHttpJsonOptions(options =>
        {
            // 中文原样输出（这是 application/json，不会被当成 HTML 嵌进页面）；
            // 枚举按名字收发、不收数字，免得前端传错一个数也能存进库里。
            options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });

        services.AddOptions<Admin.LiveStreamOptions>();
        services.AddSingleton<IHealthProbe, DatabaseHealthProbe>();
        services.AddAdminAuthentication();
        return services;
    }
}
