using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace DesktopPlanner.Infrastructure;
public sealed class PlannerDbContextFactory : IDesignTimeDbContextFactory<PlannerDbContext>
{
    public PlannerDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PlannerDbContext>()
        .UseSqlite("Data Source=planner-design.db").Options);
}
