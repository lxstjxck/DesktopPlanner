using DesktopPlanner.Domain;
using DesktopPlanner.Infrastructure;

namespace DesktopPlanner.Infrastructure.Tests;

public sealed class HabitDayMarkTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DesktopPlannerHabit", Guid.NewGuid().ToString("N"));
    private SqlitePlannerStore Create() => new(Path.Combine(directory, "planner.db"));

    [Fact]
    public async Task MarksPersistChangeDeleteAndStayInTheirMonth()
    {
        var date = new DateTime(2026, 9, 12);
        using (var store = Create())
        {
            await store.InitializeAsync();
            await store.SaveHabitDayMarkAsync(new HabitDayMark { Date = date, ColorHex = "#5CC8FF" });
            await store.SaveHabitDayMarkAsync(new HabitDayMark { Date = date, ColorHex = "#FF8FA3" });
        }
        using var reopened = Create();
        await reopened.InitializeAsync();
        Assert.Equal("#FF8FA3", Assert.Single(await reopened.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1))).ColorHex);
        Assert.Empty(await reopened.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)));
        await reopened.DeleteHabitDayMarkAsync(WidgetLayout.DefaultTrackerId, date);
        Assert.Empty(await reopened.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)));
    }

    [Fact]
    public async Task ResetClearsHabitMarks()
    {
        using var store = Create();
        await store.InitializeAsync();
        await store.SaveHabitDayMarkAsync(new HabitDayMark { Date = DateTime.Today, ColorHex = "#7EE2A8" });
        await store.ResetDataAsync();
        Assert.Empty(await store.GetHabitDayMarksAsync(WidgetLayout.DefaultTrackerId, DateTime.Today, DateTime.Today.AddDays(1)));
    }

    [Fact]
    public async Task MarksOnTheSameDayAreIndependentForEachTracker()
    {
        using var store = Create();
        await store.InitializeAsync();
        var date = new DateTime(2026, 9, 12);
        await store.SaveHabitDayMarkAsync(new HabitDayMark { TrackerId = "health", Date = date, ColorHex = "#7EE2A8" });
        await store.SaveHabitDayMarkAsync(new HabitDayMark { TrackerId = "study", Date = date, ColorHex = "#FFD166" });
        await store.DeleteHabitDayMarkAsync("health", date);
        Assert.Empty(await store.GetHabitDayMarksAsync("health", date, date.AddDays(1)));
        Assert.Equal("#FFD166", Assert.Single(await store.GetHabitDayMarksAsync("study", date, date.AddDays(1))).ColorHex);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
