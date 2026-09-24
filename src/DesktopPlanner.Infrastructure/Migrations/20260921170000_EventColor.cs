using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DesktopPlanner.Infrastructure.Migrations;

public partial class EventColor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(name: "ColorHex", table: "Events", type: "TEXT", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "ColorHex", table: "Events");
}
