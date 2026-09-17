using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "player_id",
                table: "guests",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT
                        device_id,
                        ROW_NUMBER() OVER (ORDER BY random(), device_id)::bigint - 1 AS row_index,
                        COUNT(*) OVER ()::bigint AS total_rows
                    FROM guests
                ),
                generated AS (
                    SELECT
                        device_id,
                        100000000000000
                            + row_index * (900000000000000 / total_rows)
                            + floor(random() * GREATEST(900000000000000 / total_rows, 1))::bigint AS player_id
                    FROM numbered
                )
                UPDATE guests
                SET player_id = generated.player_id
                FROM generated
                WHERE guests.device_id = generated.device_id;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "player_id",
                table: "guests",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_guests_player_id",
                table: "guests",
                column: "player_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_guests_player_id",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "player_id",
                table: "guests");
        }
    }
}
