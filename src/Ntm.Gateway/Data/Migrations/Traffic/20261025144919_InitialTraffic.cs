using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class InitialTraffic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hourly_aggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HourUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ApiTokenId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestCount = table.Column<long>(type: "INTEGER", nullable: false),
                    SuccessCount = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<long>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite5mTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite1hTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostNanoUsd = table.Column<long>(type: "INTEGER", nullable: false),
                    TtfbMillisecondsSum = table.Column<long>(type: "INTEGER", nullable: false),
                    TtfbSampleCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hourly_aggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ApiTokenId = table.Column<int>(type: "INTEGER", nullable: true),
                    ApiTokenName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PublishedModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    InboundApiFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestKind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsStream = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    CommittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientDisconnected = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite5mTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite1hTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsageSource = table.Column<int>(type: "INTEGER", nullable: false),
                    CostNanoUsd = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "attempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ChannelKeyId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ChannelKeyPrefix = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UpstreamApiFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    RequestUri = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FailureClass = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownScope = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FirstEventAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstContentEventAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Committed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Passthrough = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite5mTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite1hTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsageSource = table.Column<int>(type: "INTEGER", nullable: false),
                    MultiplierBasisPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    CostNanoUsd = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_attempts_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attempts_ChannelId_StartedAt",
                table: "attempts",
                columns: new[] { "ChannelId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_attempts_RequestId_Ordinal",
                table: "attempts",
                columns: new[] { "RequestId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates",
                columns: new[] { "HourUtc", "ChannelId", "UpstreamModel", "ApiTokenId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requests_ApiTokenId_StartedAt",
                table: "requests",
                columns: new[] { "ApiTokenId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_requests_StartedAt",
                table: "requests",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempts");

            migrationBuilder.DropTable(
                name: "hourly_aggregates");

            migrationBuilder.DropTable(
                name: "requests");
        }
    }
}
