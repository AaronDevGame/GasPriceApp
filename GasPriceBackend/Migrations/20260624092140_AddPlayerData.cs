using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_data",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<long>(type: "bigint", nullable: false),
                    health = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    money = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    position_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    inventory_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    extra_data_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_data", x => x.device_id);
                    table.ForeignKey(
                        name: "FK_player_data_guests_device_id",
                        column: x => x.device_id,
                        principalTable: "guests",
                        principalColumn: "device_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO player_data (
                    device_id,
                    player_id,
                    health,
                    money,
                    position_json,
                    inventory_json,
                    extra_data_json,
                    created_at,
                    updated_at
                )
                SELECT
                    device_id,
                    player_id,
                    100,
                    0,
                    '{}'::jsonb,
                    '[]'::jsonb,
                    '{}'::jsonb,
                    NOW(),
                    NOW()
                FROM guests;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_player_data_player_id",
                table: "player_data",
                column: "player_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_data");
        }
    }
}
