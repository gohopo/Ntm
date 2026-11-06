using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Pricing;
using Ntm.Gateway.Security;
using Ntm.Gateway.Sync;

namespace Ntm.Gateway.Data;

/// <summary>
/// <c>ntm.db</c>：只放配置（ADR-0004）。SQLite 同时只有一个写者，
/// 把批量写入的流量记录分到另一个库，配置保存就不必排在日志批次后面。
/// </summary>
public sealed class ConfigDbContext(DbContextOptions<ConfigDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<ChannelKey> ChannelKeys => Set<ChannelKey>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<UpstreamModel> UpstreamModels => Set<UpstreamModel>();

    public DbSet<PublishedModel> PublishedModels => Set<PublishedModel>();

    public DbSet<MatchRule> MatchRules => Set<MatchRule>();

    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();

    public DbSet<PriceRow> PriceRows => Set<PriceRow>();

    public DbSet<ModelSyncEvent> ModelSyncEvents => Set<ModelSyncEvent>();

    /// <summary>只有一行的全局设置（Q154）。</summary>
    public DbSet<GatewaySettings> Settings => Set<GatewaySettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var timestamp = new UnixMillisecondsConverter();
        var nullableTimestamp = new NullableUnixMillisecondsConverter();

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("channels");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.Path).HasMaxLength(500);
            entity.Property(e => e.Provider).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.UpdatedAt).HasConversion(timestamp);
            entity.Property(e => e.LastModelSyncAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.DebugCaptureUntil).HasConversion(nullableTimestamp);
            entity.Property(e => e.LastModelSyncMessage).HasMaxLength(1000);

            // 可用时段只跟着渠道一起读写，存成 JSON 列（Q81/Q84）。
            entity.OwnsMany(e => e.TimeWindows, window => window.ToJson());

            // 改写规则同样只跟着渠道一起读写（Q144）。
            entity.OwnsMany(e => e.RewriteRules, rule => rule.ToJson());
            entity.HasMany(e => e.Keys)
                .WithOne(e => e.Channel!)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.UpstreamModels)
                .WithOne(e => e.Channel!)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UpstreamModel>(entity =>
        {
            entity.ToTable("upstream_models");
            entity.HasKey(e => e.Id);

            // 大小写敏感：上游就是大小写敏感的。SQLite 默认的 BINARY 排序正好如此。
            entity.HasIndex(e => new { e.ChannelId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.LastSeenAt).HasConversion(nullableTimestamp);
        });

        modelBuilder.Entity<PublishedModel>(entity =>
        {
            entity.ToTable("published_models");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(300);
            entity.Property(e => e.DisplayName).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.UpdatedAt).HasConversion(timestamp);
            entity.HasMany(e => e.Rules)
                .WithOne(e => e.PublishedModel!)
                .HasForeignKey(e => e.PublishedModelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MatchRule>(entity =>
        {
            entity.ToTable("match_rules");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PublishedModelId, e.Ordinal });
            entity.Property(e => e.Pattern).HasMaxLength(1000);

            // 限定到某个渠道的规则随渠道一起删除：留着它只会永远匹配不到东西。
            // 排除列表里的渠道 ID 没有外键，失效的 ID 无害。
            entity.HasOne<Channel>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.PrimitiveCollection(e => e.ExcludedChannelIds);
            entity.PrimitiveCollection(e => e.ExcludedPatterns);
        });

        modelBuilder.Entity<ChannelKey>(entity =>
        {
            entity.ToTable("channel_keys");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ChannelId, e.Name }).IsUnique();

            // 同一份密钥值可能配在多个 Channel 上，冷却与限速按指纹归集（Q21），所以这个索引不唯一。
            entity.HasIndex(e => e.SecretFingerprint);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.ProtectedSecret).HasMaxLength(4000);
            entity.Property(e => e.SecretFingerprint).HasMaxLength(100);
            entity.Property(e => e.SecretPrefix).HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.PrimitiveCollection(e => e.AllowedModels);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(e => e.Id);

            // 按指纹查（ADR-0006）：库里没有 API 密钥原文。
            entity.HasIndex(e => e.Fingerprint).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Fingerprint).HasMaxLength(100);
            entity.Property(e => e.Prefix).HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.ExpiresAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.LastUsedAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.DebugCaptureUntil).HasConversion(nullableTimestamp);

            // 限制都是跟着行读写的小列表，存成 JSON 列，不建单独的 Restriction 表（Q84）。
            // 渠道 ID 没有外键：渠道删了，失效的 ID 无害。
            entity.PrimitiveCollection(e => e.AllowedModels);
            entity.PrimitiveCollection(e => e.AllowedChannelIds);
            entity.PrimitiveCollection(e => e.BlockedChannelIds);
        });

        modelBuilder.Entity<AdminAccount>(entity =>
        {
            entity.ToTable("admin_account");
            entity.HasKey(e => e.Id);

            // 只有一行，主键是常量，不要自增。
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.PasswordChangedAt).HasConversion(timestamp);
        });

        modelBuilder.Entity<PriceRow>(entity =>
        {
            entity.ToTable("prices");
            entity.HasKey(e => e.Id);

            // 一个 (Provider, UpstreamModel) 只有一行（Q48）。两列都存小写。
            entity.HasIndex(e => new { e.Provider, e.UpstreamModel }).IsUnique();
            entity.Property(e => e.Provider).HasMaxLength(100);
            entity.Property(e => e.UpstreamModel).HasMaxLength(300);
            entity.Property(e => e.MatchedModelId).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.UpdatedAt).HasConversion(timestamp);

            // 长上下文单价只跟着行一起读写，没有按它查询的需求，存成 JSON 列。
            entity.OwnsMany(e => e.LongContext, group => group.ToJson());
        });

        modelBuilder.Entity<GatewaySettings>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.DebugCaptureUntil).HasConversion(nullableTimestamp);
            entity.Property(e => e.UpdatedAt).HasConversion(timestamp);
        });

        modelBuilder.Entity<ModelSyncEvent>(entity =>
        {
            entity.ToTable("model_sync_events");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OccurredAt);
            entity.Property(e => e.OccurredAt).HasConversion(timestamp);
            entity.Property(e => e.ChannelName).HasMaxLength(200);
            entity.Property(e => e.UpstreamModel).HasMaxLength(300);
            entity.Property(e => e.PublishedModels).HasMaxLength(ModelSyncEvent.MaxPublishedModelsLength);
        });

        base.OnModelCreating(modelBuilder);
    }
}
