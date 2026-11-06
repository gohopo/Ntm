using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ntm.Gateway.Data;
using Ntm.Gateway.Security;
using Ntm.Gateway.Upstream;
using Ntm.Protocol;

namespace Ntm.Gateway.Configuration;

/// <summary>
/// 首次启动的种子配置（配置节 <c>Ntm:Seed</c>）。只在库里还没有 Channel 时读取，之后改它不会改库。
/// 有了管理页面之后它只是个便利：用 compose 起一个能直接用的实例，不必先打开页面。
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Ntm:Seed";

    public string ChannelName { get; set; } = "default";

    public string? BaseUrl { get; set; }

    public string? Path { get; set; }

    public ApiFormat ApiFormat { get; set; } = ApiFormat.OpenAiChat;

    /// <summary>上游密钥原文。落库前加密（ADR-0006）。</summary>
    public string? ChannelKey { get; set; }
}

/// <summary>
/// 首次启动：建管理员账号（Q33）；按 <see cref="SeedOptions"/> 建一个 Channel；库里没有 ApiKey 就生成一个。
/// 生成出来的管理员密码与 API 密钥都只在启动日志里出现这一次（ADR-0006：库里只存哈希与指纹）。
/// </summary>
public static partial class FirstRunSeeder
{
    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ConfigDbContext db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        ChannelKeyProtector protector = scope.ServiceProvider.GetRequiredService<ChannelKeyProtector>();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Ntm.FirstRun");
        TimeProvider time = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        DateTimeOffset now = time.GetUtcNow();

        AdminAccountService admin = scope.ServiceProvider.GetRequiredService<AdminAccountService>();
        string? generatedPassword = await admin.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (generatedPassword is not null)
        {
            LogAdminPasswordGenerated(logger, generatedPassword);
        }

        SeedOptions seed = configuration.GetSection(SeedOptions.SectionName).Get<SeedOptions>() ?? new SeedOptions();

        if (!await db.Channels.AnyAsync(cancellationToken).ConfigureAwait(false)
            && !string.IsNullOrWhiteSpace(seed.ChannelKey))
        {
            if (!UpstreamAddress.TryNormalizeBaseUrl(seed.BaseUrl, out string? baseUrl, out string versionPrefix))
            {
                throw new InvalidOperationException($"{SeedOptions.SectionName}:BaseUrl 不是合法的 http(s) 地址。");
            }

            string secret = seed.ChannelKey.Trim();
            var channel = new Channel
            {
                Name = seed.ChannelName,
                Enabled = true,
                BaseUrl = baseUrl,
                Path = UpstreamAddress.ResolveChannelPath(seed.Path, versionPrefix, seed.ApiFormat),
                ApiFormat = seed.ApiFormat,
                CreatedAt = now,
                UpdatedAt = now,
            };
            channel.Keys.Add(new ChannelKey
            {
                Name = "default",
                ProtectedSecret = protector.Protect(secret),
                SecretFingerprint = SecretFingerprint.Compute(secret),
                SecretPrefix = SecretFingerprint.Prefix(secret),
                Enabled = true,
                CreatedAt = now,
            });

            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogChannelSeeded(logger, channel.Name, baseUrl, channel.Path ?? "(按 API 格式默认)");
        }

        if (!await db.ApiKeys.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            string token = SecretFingerprint.GenerateApiKey();
            db.ApiKeys.Add(new ApiKey
            {
                Name = "default",
                Fingerprint = SecretFingerprint.Compute(token),
                Prefix = SecretFingerprint.Prefix(token),
                Enabled = true,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogApiKeyGenerated(logger, token);
        }
    }

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Warning,
        Message = "已生成管理员密码（只显示这一次，库里只存哈希；忘了用 reset-admin-password 命令重置）：{Password}")]
    private static partial void LogAdminPasswordGenerated(ILogger logger, string password);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "已按种子配置创建渠道 {Channel}：{BaseUrl} {Path}")]
    private static partial void LogChannelSeeded(ILogger logger, string channel, string baseUrl, string path);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "已生成 API 密钥（只显示这一次，库里只存指纹）：{ApiKey}")]
    private static partial void LogApiKeyGenerated(ILogger logger, string apiKey);
}
