using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ModelSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastModelSyncAdded",
                table: "channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LastModelSyncAt",
                table: "channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastModelSyncMessage",
                table: "channels",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastModelSyncRemoved",
                table: "channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "LastModelSyncSucceeded",
                table: "channels",
                type: "INTEGER",
                nullable: true);

            // 已有渠道填 false：升级不该让它们的模型清单突然开始变。新渠道由实体默认值开（Q135）。
            migrationBuilder.AddColumn<bool>(
                name: "ModelSyncEnabled",
                table: "channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "model_sync_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PublishedModels = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_sync_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_sync_events_OccurredAt",
                table: "model_sync_events",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_sync_events");

            migrationBuilder.DropColumn(
                name: "LastModelSyncAdded",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "LastModelSyncAt",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "LastModelSyncMessage",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "LastModelSyncRemoved",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "LastModelSyncSucceeded",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "ModelSyncEnabled",
                table: "channels");
        }
    }
}
