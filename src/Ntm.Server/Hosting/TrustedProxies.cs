using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Ntm.Server.Hosting;

/// <summary>
/// 部署在 HTTPS 反向代理后面时，只采信列出来的代理发来的 <c>X-Forwarded-Proto</c> / <c>X-Forwarded-For</c>（Q129）。
/// 默认为空：两个头一律忽略。否则端口直接暴露时，谁都能伪造来源地址绕开登录限流。
/// </summary>
internal static class TrustedProxies
{
    /// <summary>逗号或空白分隔的 IP 或 CIDR，例如 <c>172.16.0.0/12, 10.0.0.1</c>。</summary>
    public const string Variable = "NTM_TRUSTED_PROXIES";

    /// <exception cref="FormatException">有一项不是 IP 或 CIDR。宁可启动失败，也不悄悄少信一个代理。</exception>
    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var networks = new List<IPNetwork>();
        foreach (string item in value.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPNetwork.TryParse(item, out IPNetwork network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(item, out IPAddress? address))
            {
                networks.Add(new IPNetwork(address, address.GetAddressBytes().Length * 8));
            }
            else
            {
                throw new FormatException($"{Variable} 里的「{item}」不是 IP 地址或 CIDR。");
            }
        }

        return networks;
    }

    public static IServiceCollection AddNtmTrustedProxies(this IServiceCollection services)
    {
        // 选项在中间件建管道时（启动时）才解析：写错的值照样让启动失败，测试也能在注册之后再改配置。
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            IReadOnlyList<IPNetwork> networks = Parse(configuration[Variable]);

            // 框架默认信任本机回环；这里只信显式列出的。
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            // 名单为空时必须整个关掉：中间件在两个名单都为空时会信任所有来源。
            if (networks.Count == 0)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (IPNetwork network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }

            // 多层代理（CDN → nginx）时一层层往回剥，直到遇到不在名单里的地址为止。
            options.ForwardLimit = null;
        });

        return services;
    }
}
