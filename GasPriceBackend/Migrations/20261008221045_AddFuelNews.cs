using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFuelNews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fuel_news",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    topic_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_news", x => x.id);
                    table.ForeignKey(
                        name: "FK_fuel_news_fuel_news_superseded_by_id",
                        column: x => x.superseded_by_id,
                        principalTable: "fuel_news",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fuel_news_subscription",
                columns: table => new
                {
                    app_instance_id = table.Column<string>(type: "text", nullable: false),
                    push_token = table.Column<string>(type: "character varying(230)", maxLength: 230, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    include_forecasts = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_news_subscription", x => x.app_instance_id);
                    table.ForeignKey(
                        name: "FK_fuel_news_subscription_guests_app_instance_id",
                        column: x => x.app_instance_id,
                        principalTable: "guests",
                        principalColumn: "app_instance_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fuel_news_revision",
                columns: table => new
                {
                    news_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    saved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_news_revision", x => new { x.news_id, x.revision });
                    table.ForeignKey(
                        name: "FK_fuel_news_revision_fuel_news_news_id",
                        column: x => x.news_id,
                        principalTable: "fuel_news",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fuel_news_delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    news_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    app_instance_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ticket_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_news_delivery", x => x.id);
                    table.ForeignKey(
                        name: "FK_fuel_news_delivery_fuel_news_revision_news_id_revision",
                        columns: x => new { x.news_id, x.revision },
                        principalTable: "fuel_news_revision",
                        principalColumns: new[] { "news_id", "revision" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fuel_news_delivery_fuel_news_subscription_app_instance_id",
                        column: x => x.app_instance_id,
                        principalTable: "fuel_news_subscription",
                        principalColumn: "app_instance_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_import_key",
                table: "fuel_news",
                column: "import_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_published_at_utc_id",
                table: "fuel_news",
                columns: new[] { "published_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_superseded_by_id",
                table: "fuel_news",
                column: "superseded_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_topic_key_status",
                table: "fuel_news",
                columns: new[] { "topic_key", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_delivery_app_instance_id",
                table: "fuel_news_delivery",
                column: "app_instance_id");

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_delivery_news_id_revision_app_instance_id",
                table: "fuel_news_delivery",
                columns: new[] { "news_id", "revision", "app_instance_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_delivery_status_next_attempt_at_utc",
                table: "fuel_news_delivery",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_news_subscription_push_token",
                table: "fuel_news_subscription",
                column: "push_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fuel_news_delivery");

            migrationBuilder.DropTable(
                name: "fuel_news_revision");

            migrationBuilder.DropTable(
                name: "fuel_news_subscription");

            migrationBuilder.DropTable(
                name: "fuel_news");
        }
    }
}
