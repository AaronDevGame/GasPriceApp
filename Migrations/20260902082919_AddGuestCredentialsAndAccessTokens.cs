using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestCredentialsAndAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "token",
                table: "guests",
                newName: "legacy_token");

            migrationBuilder.AlterColumn<string>(
                name: "legacy_token",
                table: "guests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTime>(
                name: "access_token_expires_at",
                table: "guests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "access_token_hash",
                table: "guests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "guest_credential_hash",
                table: "guests",
                type: "text",
                nullable: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_token_expires_at",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "access_token_hash",
                table: "guests");

            migrationBuilder.DropColumn(
                name: "guest_credential_hash",
                table: "guests");

            migrationBuilder.AlterColumn<string>(
                name: "legacy_token",
                table: "guests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "legacy_token",
                table: "guests",
                newName: "token");
        }
    }
}
