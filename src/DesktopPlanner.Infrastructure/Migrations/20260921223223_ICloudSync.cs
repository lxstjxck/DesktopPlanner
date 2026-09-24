using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DesktopPlanner.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ICloudSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReadOnly",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RawICalendar",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CalendarId_ExternalId",
                table: "Events",
                columns: new[] { "CalendarId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_CalendarId_ExternalId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "IsReadOnly",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RawICalendar",
                table: "Events");
        }
    }
}
