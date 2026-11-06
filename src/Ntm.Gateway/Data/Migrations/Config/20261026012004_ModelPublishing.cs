using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ModelPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "published_models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RequestKind = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "upstream_models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upstream_models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_upstream_models_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "match_rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublishedModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExcludedChannelIds = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedPatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    Weight = table.Column<int>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_match_rules_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_match_rules_published_models_PublishedModelId",
                        column: x => x.PublishedModelId,
                        principalTable: "published_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_match_rules_ChannelId",
                table: "match_rules",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_match_rules_PublishedModelId_Ordinal",
                table: "match_rules",
                columns: new[] { "PublishedModelId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_published_models_Name",
                table: "published_models",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upstream_models_ChannelId_Name",
                table: "upstream_models",
                columns: new[] { "ChannelId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_rules");

            migrationBuilder.DropTable(
                name: "upstream_models");

            migrationBuilder.DropTable(
                name: "published_models");
        }
    }
}
