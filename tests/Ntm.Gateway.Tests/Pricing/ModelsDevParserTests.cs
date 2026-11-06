using System.Text;
using Ntm.Gateway.Pricing;

namespace Ntm.Gateway.Tests.Pricing;

/// <summary>models.dev 的 api.json 解析（Q65/Q66/Q72）。样本照实际目录的形状裁剪。</summary>
public sealed class ModelsDevParserTests
{
    private const string Sample = """
        {
          "anthropic": {
            "id": "anthropic",
            "name": "Anthropic",
            "models": {
              "claude-sonnet-4-5": {
                "id": "claude-sonnet-4-5",
                "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 }
              },
              "claude-haiku-5-5": {
                "id": "claude-haiku-5-5",
                "cost": {
                  "input": 0.1, "output": 0.5, "cache_read": 0.01, "cache_write": 0.125,
                  "tiers": [ { "input": 0.5, "output": 2.5, "tier": { "type": "context", "size": 100000 } } ]
                }
              },
              "claude-no-cost": { "id": "claude-no-cost" }
            }
          },
          "google": {
            "id": "google",
            "name": "Google",
            "models": {
              "gemini-2.5-pro": {
                "id": "gemini-2.5-pro",
                "cost": {
                  "input": 1.25, "output": 10, "cache_read": 0.125, "reasoning": 10, "input_audio": 7,
                  "tiers": [ { "input": 2.5, "output": 15, "cache_read": 0.25, "tier": { "type": "context", "size": 200001 } } ],
                  "context_over_200k": { "input": 2.5, "output": 15, "cache_read": 0.25 }
                }
              }
            }
          },
          "openai": {
            "id": "openai",
            "name": "OpenAI",
            "models": {
              "gpt-4.1": { "id": "gpt-4.1", "cost": { "input": 2, "output": 8, "cache_read": 0.5 } },
              "gpt-free": { "id": "gpt-free", "cost": { "input": 0, "output": 0 } }
            }
          },
          "openrouter": {
            "id": "openrouter",
            "name": "OpenRouter",
            "models": {
              "anthropic/claude-sonnet-4.5": {
                "id": "anthropic/claude-sonnet-4.5",
                "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 }
              }
            }
          }
        }
        """;

    private static async Task<ParsedCatalog> ParseAsync(params string[] keep)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sample));
        return await ModelsDevParser.ParseAsync(stream, keep.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryProviderIsListedButOnlyTheKeptOnesCarryPrices()
    {
        ParsedCatalog parsed = await ParseAsync("openai");

        Assert.Equal(["anthropic", "google", "openai", "openrouter"], parsed.Providers.Select(p => p.Id).Order());
        Assert.Equal("OpenAI", parsed.Providers.Single(p => p.Id == "openai").Name);

        // 没有 cost 的模型不计数。
        Assert.Equal(2, parsed.Providers.Single(p => p.Id == "anthropic").ModelCount);

        Assert.All(parsed.Prices, p => Assert.Equal("openai", p.Provider));
        Assert.Equal(["gpt-4.1", "gpt-free"], parsed.Prices.Select(p => p.ModelId).Order());
    }

    [Fact]
    public async Task FieldsMapOntoTheFiveUsagePartsAndZeroIsNotEmpty()
    {
        ParsedCatalog parsed = await ParseAsync("openai");

        PriceSet gpt = parsed.Prices.Single(p => p.ModelId == "gpt-4.1").Prices;
        Assert.Equal(new PriceSet(2m, 0.5m, null, null, 8m), gpt);

        // 0 是免费，不是不知道（Q65）。
        PriceSet free = parsed.Prices.Single(p => p.ModelId == "gpt-free").Prices;
        Assert.Equal(0m, free.Input);
        Assert.Equal(0m, free.Output);
        Assert.Null(free.CacheRead);
    }

    [Fact]
    public async Task ClaudeGetsAnHourLongCacheWriteAtTwiceTheInputPrice()
    {
        ParsedCatalog parsed = await ParseAsync("anthropic", "openrouter");

        PriceSet sonnet = parsed.Prices.Single(p => p.ModelId == "claude-sonnet-4-5").Prices;
        Assert.Equal(new PriceSet(3m, 0.3m, 3.75m, 6m, 15m), sonnet);

        // 中转商下面的 claude 行同样推出 1 小时缓存写。
        PriceSet routed = parsed.Prices.Single(p => p.Provider == "openrouter").Prices;
        Assert.Equal(6m, routed.CacheWrite1h);
    }

    [Fact]
    public async Task LongContextPricesAreNormalizedDedupedAndInheritWhatTheyDoNotList()
    {
        ParsedCatalog parsed = await ParseAsync("google", "anthropic");

        // 200001 和 context_over_200k 是同一档，只留一个，门槛记成 200000。
        CatalogPrice gemini = parsed.Prices.Single(p => p.ModelId == "gemini-2.5-pro");
        LongContextPrice tier = Assert.Single(gemini.LongContext);
        Assert.Equal(200_000, tier.InputTokensOver);
        Assert.Equal(new PriceSet(2.5m, 0.25m, null, null, 15m), tier.Prices);

        // 这一档没列缓存读 / 缓存写，就用基础单价；1 小时缓存写按这一档自己的输入价推。
        CatalogPrice haiku = parsed.Prices.Single(p => p.ModelId == "claude-haiku-5-5");
        LongContextPrice haikuTier = Assert.Single(haiku.LongContext);
        Assert.Equal(100_000, haikuTier.InputTokensOver);
        Assert.Equal(new PriceSet(0.5m, 0.01m, 0.125m, 1m, 2.5m), haikuTier.Prices);
        Assert.Equal(0.2m, haiku.Prices.CacheWrite1h);
    }

    [Theory]
    [InlineData(200_000L, 200_000L)]
    [InlineData(200_001L, 200_000L)]
    [InlineData(272_000L, 272_000L)]
    [InlineData(128_001L, 128_000L)]
    public void ThresholdsOneAboveARoundNumberMeanTheRoundNumber(long raw, long expected) =>
        Assert.Equal(expected, ModelsDevParser.NormalizeThreshold(raw));

    [Fact]
    public async Task ReasoningIsTakenAsTheThinkingOutputPriceOnlyWhenAboveOutput()
    {
        const string sample = """
            {
              "alibaba": {
                "id": "alibaba",
                "name": "Alibaba",
                "models": {
                  "qwen-plus": {
                    "id": "qwen-plus",
                    "cost": {
                      "input": 0.4, "output": 1.2, "reasoning": 4, "cache_read": 0.08,
                      "tiers": [
                        { "input": 1.2, "output": 3.6, "reasoning": 12, "tier": { "type": "context", "size": 256000 } },
                        { "input": 2, "output": 6, "tier": { "type": "context", "size": 512000 } }
                      ]
                    }
                  },
                  "sonar-deep-research": { "id": "sonar-deep-research", "cost": { "input": 2, "output": 8, "reasoning": 3 } },
                  "kilo-placeholder": { "id": "kilo-placeholder", "cost": { "input": 1, "output": 2, "reasoning": 0 } },
                  "same-price": { "id": "same-price", "cost": { "input": 1, "output": 10, "reasoning": 10 } }
                }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sample));
        ParsedCatalog parsed = await ModelsDevParser.ParseAsync(stream, new HashSet<string>(StringComparer.Ordinal) { "alibaba" });
        CatalogPrice Of(string id) => parsed.Prices.Single(p => p.ModelId == id);

        // 阿里系：整个输出在思考模式下的单价，比平时高（ADR-0013）。
        CatalogPrice qwen = Of("qwen-plus");
        Assert.Equal(4m, qwen.Prices.ThinkingOutput);
        Assert.Equal(12m, qwen.LongContext[0].Prices.ThinkingOutput);

        // 没列 reasoning 的档继承基础的思考价，但只在仍然高于这一档的输出价时才算数。
        Assert.Null(qwen.LongContext[1].Prices.ThinkingOutput);

        // 比输出价低（推理 Token 单独计价的另一种规则）、是 0（占位）、或相等：都不取。
        Assert.Null(Of("sonar-deep-research").Prices.ThinkingOutput);
        Assert.Null(Of("kilo-placeholder").Prices.ThinkingOutput);
        Assert.Null(Of("same-price").Prices.ThinkingOutput);
    }
}
