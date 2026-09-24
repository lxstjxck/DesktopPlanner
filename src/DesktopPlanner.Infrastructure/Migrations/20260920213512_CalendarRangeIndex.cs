using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DesktopPlanner.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CalendarRangeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Events_Start_End",
                table: "Events",
                columns: new[] { "Start", "End" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_Start_End",
                table: "Events");
        }
    }
}
