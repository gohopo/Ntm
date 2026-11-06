using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class ResponseLandings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponseId",
                table: "attempts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "response_landings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ApiKeyId = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AwaitingUsage = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_response_landings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_response_landings_LastUsedAt",
                table: "response_landings",
                column: "LastUsedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "response_landings");

            migrationBuilder.DropColumn(
                name: "ResponseId",
                table: "attempts");
        }
    }
}
