using System.Text.RegularExpressions;
using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Routing;

/// <summary>
/// 编译好的一条匹配模式（ADR-0011）。精确名按序数比较；正则完全锚定，用 NonBacktracking 引擎。
///
/// 用户写的 <c>^</c>、<c>$</c> 接受并规范化，<see cref="Effective"/> 给出实际生效的模式，
/// 预览界面原样展示它——AxonHub 会悄悄改写用户的模式，用户看不到。
/// </summary>
public sealed class ModelPattern
{
    /// <summary>NonBacktracking 是线性的，这个超时只是兜底。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly string? _exact;
    private readonly Regex? _regex;

    private ModelPattern(MatchKind kind, string effective, string? exact, Regex? regex)
    {
        Kind = kind;
        Effective = effective;
        _exact = exact;
        _regex = regex;
    }

    public MatchKind Kind { get; }

    /// <summary>实际生效的模式。精确名就是名字本身。</summary>
    public string Effective { get; }

    /// <summary>
    /// 编译模式。失败时返回 false 并给出给人看的原因：语法错误，或用了 NonBacktracking 不支持的
    /// 前后查找、反向引用、原子组。保存配置时用它拒绝非法模式；构建快照时用它把坏规则标出来，而不是静默匹配空集。
    /// </summary>
    public static bool TryCreate(MatchKind kind, string pattern, out ModelPattern? compiled, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        compiled = null;
        error = null;

        if (pattern.Length == 0)
        {
            error = "模式不能为空。";
            return false;
        }

        if (kind == MatchKind.Exact)
        {
            compiled = new ModelPattern(kind, pattern, pattern, null);
            return true;
        }

        string effective = Anchor(pattern);
        try
        {
            var regex = new Regex(
                effective,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                MatchTimeout);
            compiled = new ModelPattern(kind, effective, null, regex);
            return true;
        }
        catch (NotSupportedException)
        {
            error = "正则用了不支持的写法（前后查找、反向引用或原子组）。需要排除某些名字时请用「排除模式」。";
            return false;
        }
        catch (ArgumentException ex)
        {
            error = "正则语法错误：" + ex.Message;
            return false;
        }
    }

    /// <summary>超时当作不匹配：宁可少一个候选，也不让一条规则卡住快照构建。</summary>
    public bool IsMatch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_exact is not null)
        {
            return string.Equals(_exact, name, StringComparison.Ordinal);
        }

        try
        {
            return _regex!.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// 包成 <c>^(?:…)$</c>。开头的内联选项（如 <c>(?i)</c>）留在锚点外面；
    /// 用户自己写的开头 <c>^</c> 和结尾未转义的 <c>$</c> 去掉，免得变成 <c>^(?:^gpt)$</c> 这种看着别扭的样子。
    /// </summary>
    internal static string Anchor(string pattern)
    {
        string modifier = string.Empty;
        string body = pattern;

        if (body.StartsWith("(?", StringComparison.Ordinal))
        {
            int close = body.IndexOf(')', StringComparison.Ordinal);
            if (close > 2 && IsInlineOptions(body.AsSpan(2, close - 2)))
            {
                modifier = body[..(close + 1)];
                body = body[(close + 1)..];
            }
        }

        if (body.StartsWith('^'))
        {
            body = body[1..];
        }

        if (body.EndsWith('$') && !IsEscaped(body, body.Length - 1))
        {
            body = body[..^1];
        }

        return modifier + "^(?:" + body + ")$";
    }

    private static bool IsInlineOptions(ReadOnlySpan<char> options)
    {
        foreach (char c in options)
        {
            if (c is not ('i' or 'm' or 'n' or 's' or 'x' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    // 前面连续反斜杠的个数是奇数，这个字符就是被转义的。
    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }
}
