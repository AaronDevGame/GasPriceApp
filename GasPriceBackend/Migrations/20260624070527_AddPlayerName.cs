using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "player_name",
                table: "guests",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    guest_count integer;
                BEGIN
                    SELECT COUNT(*) INTO guest_count FROM guests;

                    IF guest_count > 9000 THEN
                        RAISE EXCEPTION 'Cannot generate unique Player #### names for more than 9000 existing guests.';
                    END IF;
                END $$;

                WITH available_names AS (
                    SELECT
                        'Player ' || number AS player_name,
                        ROW_NUMBER() OVER (ORDER BY random()) AS row_index
                    FROM generate_series(1000, 9999) AS numbers(number)
                ),
                numbered_guests AS (
                    SELECT
                        device_id,
                        ROW_NUMBER() OVER (ORDER BY random(), device_id) AS row_index
                    FROM guests
                )
                UPDATE guests
                SET player_name = available_names.player_name
                FROM numbered_guests
                JOIN available_names ON available_names.row_index = numbered_guests.row_index
                WHERE guests.device_id = numbered_guests.device_id;
                """);

            migrationBuilder.Sql("""
                CREATE TABLE guests_reordered (
                    device_id text NOT NULL,
                    token text NOT NULL,
                    player_id bigint NOT NULL,
                    player_name character varying(24) NOT NULL,
                    ip_address text NULL,
                    user_agent text NULL,
                    device_type text NULL,
                    created_at timestamp with time zone NOT NULL,
                    last_login_at timestamp with time zone NOT NULL,
                    login_count integer NOT NULL,
                    last_logout_at timestamp with time zone NULL,
                    logout_count integer NOT NULL
                );

                INSERT INTO guests_reordered (
                    device_id,
                    token,
                    player_id,
                    player_name,
                    ip_address,
                    user_agent,
                    device_type,
                    created_at,
                    last_login_at,
                    login_count,
                    last_logout_at,
                    logout_count
                )
                SELECT
                    device_id,
                    token,
                    player_id,
                    player_name,
                    ip_address,
                    user_agent,
                    device_type,
                    created_at,
                    last_login_at,
                    login_count,
                    last_logout_at,
                    logout_count
                FROM guests;

                DROP TABLE guests;
                ALTER TABLE guests_reordered RENAME TO guests;
                ALTER TABLE guests ADD CONSTRAINT "PK_guests" PRIMARY KEY (device_id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_guests_player_id",
                table: "guests",
                column: "player_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guests_player_name",
                table: "guests",
                column: "player_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_guests_player_name",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "player_name",
                table: "guests");
        }
    }
}
