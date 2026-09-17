using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class RenameDeviceIdToAppInstanceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_player_data_guests_device_id",
                table: "player_data");

            migrationBuilder.RenameColumn(
                name: "device_id",
                table: "player_data",
                newName: "app_instance_id");

            migrationBuilder.RenameColumn(
                name: "device_id",
                table: "guests",
                newName: "app_instance_id");

            migrationBuilder.AddForeignKey(
                name: "FK_player_data_guests_app_instance_id",
                table: "player_data",
                column: "app_instance_id",
                principalTable: "guests",
                principalColumn: "app_instance_id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_player_data_guests_app_instance_id",
                table: "player_data");

            migrationBuilder.RenameColumn(
                name: "app_instance_id",
                table: "player_data",
                newName: "device_id");

            migrationBuilder.RenameColumn(
                name: "app_instance_id",
                table: "guests",
                newName: "device_id");

            migrationBuilder.AddForeignKey(
                name: "FK_player_data_guests_device_id",
                table: "player_data",
                column: "device_id",
                principalTable: "guests",
                principalColumn: "device_id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
