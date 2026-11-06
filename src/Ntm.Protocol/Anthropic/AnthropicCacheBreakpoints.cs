using Ntm.Protocol.Ir;

namespace Ntm.Protocol.Anthropic;

/// <summary>
/// 写 Anthropic 请求时把缓存断点（CacheBreakpoint）落在哪些位置（Q242–Q246，ADR-0019）。
/// <para>
/// 断点按前缀顺序编号：先是工具定义，再是 system 块，最后是各条消息里会写出去的块。
/// 编号只看 <see cref="AnthropicRequestWriter"/> 真正写出去的东西，写出器按同样的顺序取断点。
/// </para>
/// <list type="bullet">
/// <item>客户端写了断点就只用客户端的；一个都没写、渠道又开着自动断点时，网关自己加。</item>
/// <item>自动加的：最后一个工具、最后一个 system 块、最后一条消息的最后一个块、
/// 最后一条消息之前那条 user 消息的最后一个块，都是 5 分钟。</item>
/// <item>思考块不能放断点，遇到就往前找；空文本块本来就不写出去。</item>
/// <item>最多 4 个：保留最后一个工具断点、最后一个 system 断点、最后两个消息断点，其余从最早的删起。</item>
/// <item>1 小时的断点必须排在 5 分钟的前面，排在后面的改成 5 分钟。</item>
/// </list>
/// </summary>
internal static class AnthropicCacheBreakpoints
{
    /// <summary>Anthropic 一个请求最多认 4 个断点，多了返回 400。</summary>
    public const int MaxBreakpoints = 4;

    /// <param name="maxBreakpoints">断点上限。渠道改写规则会写顶层 cache_control（自动缓存，自己占一个）时传 3（Q307）。</param>
    public static AnthropicCachePlan Plan(IrRequest request, bool auto, int maxBreakpoints = MaxBreakpoints)
    {
        List<Slot> slots = Enumerate(request, out int systemBase, out int messageBase);
        var marks = new SortedDictionary<int, IrCacheTtl>();

        if (slots.Any(s => s.Client is not null))
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Eligible && slots[i].Client is { } ttl)
                {
                    marks[i] = ttl;
                }
            }
        }
        else if (auto)
        {
            MarkLast(slots, marks, s => s.Section == Section.Tool);
            MarkLast(slots, marks, s => s.Section == Section.System);

            int lastMessage = slots.LastOrDefault(s => s.Section == Section.Message).Message;
            int last = LastEligible(slots, s => s.Section == Section.Message);
            if (last >= 0)
            {
                marks[last] = IrCacheTtl.FiveMinutes;

                // 最后一条消息之前的那条 user 消息：一轮新增的块很多时，最后那个断点往前找不到上一轮的缓存，靠它接上。
                int previousUser = slots
                    .Where(s => s.Section == Section.Message && s.Role == IrRole.User && s.Message < lastMessage)
                    .Select(s => s.Message)
                    .DefaultIfEmpty(-1)
                    .Max();
                int earlier = previousUser < 0
                    ? -1
                    : LastEligible(slots, s => s.Section == Section.Message && s.Message <= previousUser);
                if (earlier >= 0)
                {
                    marks[earlier] = IrCacheTtl.FiveMinutes;
                }
            }
        }

        Trim(slots, marks, maxBreakpoints);
        OrderTtls(marks);
        return new AnthropicCachePlan(systemBase, messageBase, marks);
    }

    /// <summary>这个块会不会写进 Anthropic 请求。和 <see cref="AnthropicRequestWriter"/> 的写法一致。</summary>
    public static bool Writes(IrBlock block) => block switch
    {
        IrText text => text.Text.Length > 0,
        IrReasoning reasoning => reasoning.SignedFor(ApiFormat.Anthropic) && reasoning.Text.Length > 0,
        IrImage or IrFile or IrToolCall or IrToolResult or IrRedactedReasoning => true,
        _ => false,
    };

    private static List<Slot> Enumerate(IrRequest request, out int systemBase, out int messageBase)
    {
        List<Slot> slots = [];
        foreach (IrTool tool in request.Tools)
        {
            slots.Add(new Slot(Section.Tool, -1, IrRole.User, true, tool.Cache));
        }

        systemBase = slots.Count;
        for (int i = 0; i < request.System.Count; i++)
        {
            if (request.System[i].Length > 0)
            {
                IrCacheTtl? cache = i < request.SystemCache.Count ? request.SystemCache[i] : null;
                slots.Add(new Slot(Section.System, -1, IrRole.User, true, cache));
            }
        }

        messageBase = slots.Count;
        int written = 0;
        foreach (IrMessage message in request.Messages)
        {
            if (!message.Content.Any(Writes))
            {
                continue;
            }

            foreach (IrBlock block in message.Content)
            {
                if (Writes(block))
                {
                    bool eligible = block is not (IrReasoning or IrRedactedReasoning);
                    slots.Add(new Slot(Section.Message, written, message.Role, eligible, block.Cache));
                }
            }

            written++;
        }

        return slots;
    }

    private static void MarkLast(List<Slot> slots, SortedDictionary<int, IrCacheTtl> marks, Func<Slot, bool> where)
    {
        int index = LastEligible(slots, where);
        if (index >= 0)
        {
            marks[index] = IrCacheTtl.FiveMinutes;
        }
    }

    private static int LastEligible(List<Slot> slots, Func<Slot, bool> where)
    {
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (slots[i].Eligible && where(slots[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static void Trim(List<Slot> slots, SortedDictionary<int, IrCacheTtl> marks, int max)
    {
        if (marks.Count <= max)
        {
            return;
        }

        var keep = new HashSet<int>();
        int[] tool = [.. marks.Keys.Where(k => slots[k].Section == Section.Tool)];
        int[] system = [.. marks.Keys.Where(k => slots[k].Section == Section.System)];
        int[] message = [.. marks.Keys.Where(k => slots[k].Section == Section.Message)];
        if (tool.Length > 0)
        {
            keep.Add(tool[^1]);
        }

        if (system.Length > 0)
        {
            keep.Add(system[^1]);
        }

        foreach (int k in message.TakeLast(2))
        {
            keep.Add(k);
        }

        foreach (int k in marks.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            if (marks.Count <= max)
            {
                break;
            }

            marks.Remove(k);
        }

        // 上限是 3 时，tool、system 加两个消息断点也会超：先去掉靠前的消息断点，最后一个消息断点总留着。
        foreach (int k in message.SkipLast(1))
        {
            if (marks.Count <= max)
            {
                break;
            }

            marks.Remove(k);
        }

        // 规则自己占了 2 个以上时（Q316）：再去掉 tool、system 的，最后一个消息断点最后才去。
        foreach (int k in tool.Concat(system).Concat(message.TakeLast(1)))
        {
            if (marks.Count <= max)
            {
                break;
            }

            marks.Remove(k);
        }
    }

    private static void OrderTtls(SortedDictionary<int, IrCacheTtl> marks)
    {
        bool sawShort = false;
        foreach (int k in marks.Keys.ToArray())
        {
            if (marks[k] == IrCacheTtl.FiveMinutes)
            {
                sawShort = true;
            }
            else if (sawShort)
            {
                marks[k] = IrCacheTtl.FiveMinutes;
            }
        }
    }

    private enum Section
    {
        Tool,
        System,
        Message,
    }

    private readonly record struct Slot(Section Section, int Message, IrRole Role, bool Eligible, IrCacheTtl? Client);
}

/// <summary>断点落在哪些编号上。工具从 0 开始，system 从 <see cref="SystemBase"/>，消息块从 <see cref="MessageBase"/>。</summary>
internal sealed record AnthropicCachePlan(int SystemBase, int MessageBase, IReadOnlyDictionary<int, IrCacheTtl> Marks)
{
    public IrCacheTtl? At(int slot) => Marks.TryGetValue(slot, out IrCacheTtl ttl) ? ttl : null;
}
