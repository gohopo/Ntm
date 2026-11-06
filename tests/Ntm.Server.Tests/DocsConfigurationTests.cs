using System.Reflection;
using System.Text.RegularExpressions;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Debugging;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Sync;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Tests;

/// <summary>
/// 文档站的配置参考页要和代码里的配置项一一对应（Q326）：漏写或写了不存在的项都算错。
/// </summary>
public sealed partial class DocsConfigurationTests
{
    private static readonly (string Section, Type Options)[] OptionClasses =
    [
        (NtmDataOptions.SectionName, typeof(NtmDataOptions)),
        (RetentionOptions.SectionName, typeof(RetentionOptions)),
        (DebugCaptureOptions.SectionName, typeof(DebugCaptureOptions)),
        (PricingOptions.SectionName, typeof(PricingOptions)),
        (ModelSyncOptions.SectionName, typeof(ModelSyncOptions)),
        (SeedOptions.SectionName, typeof(SeedOptions)),
        (Ntm.Server.Hosting.MetricsOptions.SectionName, typeof(Ntm.Server.Hosting.MetricsOptions)),
    ];

    [Fact]
    public void TheConfigurationPageListsEveryOptionAndNothingElse()
    {
        string page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "docs", "src", "routes", "configuration", "+page.svelte"));

        HashSet<string> expected = OptionClasses
            .SelectMany(c => c.Options
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.SetMethod!.IsPublic)
                .Select(p => c.Section.Replace(":", "__", StringComparison.Ordinal) + "__" + p.Name))
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> documented = EnvironmentKey().Matches(page)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(expected.Except(documented).Order(StringComparer.Ordinal));
        Assert.Empty(documented.Except(expected).Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntm.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根目录（Ntm.slnx）。");
    }

    [GeneratedRegex(@"\bNtm__[A-Za-z]+__[A-Za-z]+\b")]
    private static partial Regex EnvironmentKey();
}
