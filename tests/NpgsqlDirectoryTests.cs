using Npgsql;
using Ticket.Adapter.Npgsql;
using Ticket.Data;
using Xunit;

namespace Ticket.Adapter.Npgsql.Tests;

public class NpgsqlDirectoryTests
{
    private static string? Pg => Environment.GetEnvironmentVariable("TEST_POSTGRES");

    private static NpgsqlDataSource Connect()
    {
        if (Pg is null) return null!;
        return NpgsqlDataSource.Create(Pg);
    }

    private static NpgsqlDirectory Directory(NpgsqlDataSource db)
    {
        using var cmd = db.CreateCommand("""
            drop table if exists it_staff_absence, it_staff_skill, it_staff, ticket_category cascade
            """);
        cmd.ExecuteNonQuery();
        return new NpgsqlDirectory(db);
    }

    private static void Exec(NpgsqlDataSource db, string sql)
    {
        using var cmd = db.CreateCommand(sql);
        cmd.ExecuteNonQuery();
    }

    [Collection("postgres")]
    public class SheetReplaceAndRoute
    {
        [Fact]
        public void RoundTrips_AllFourTabs_FromSheetRows()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("ticket_category",
            [
                ["Category Slug", "Description", "Priority Code"],
                ["network", "wifi and VPN", ""],
                ["hardware", "printers", "urgent"],
            ]);
            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Active"],
                ["100", "Alice", "TRUE"],
                ["200", "Bob", "TRUE"],
            ]);
            directory.ReplaceFromSheet("it_staff_skill",
            [
                ["User Id", "Category Slug"],
                ["100", "network"],
                ["200", "hardware"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["User Id", "From Utc", "Until Utc", "Note"],
                ["200", "2026-10-07", "2026-10-07", "out for Oct 7"],
            ]);

            Assert.Equal(
            [
                new TicketCategory("hardware", "printers"),
                new TicketCategory("network", "wifi and VPN"),
            ], directory.Categories());

            var routed = directory.Route("network", new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL], routed.StaffIds);

            var withoutBob = directory.Route("hardware", new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL], withoutBob.StaffIds);

            var networkNextDay = directory.Route("network", new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL], networkNextDay.StaffIds);

            var both = directory.Route("account-access", new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL, 200UL], both.StaffIds);
        }

        [Fact]
        public void Route_FallsBackToAvailableGeneralists_WhenNoSpecialistMatches()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Active"],
                ["100", "Alice", "TRUE"],
                ["200", "Bob", "TRUE"],
                ["300", "Carol", "FALSE"],
            ]);
            directory.ReplaceFromSheet("it_staff_skill",
            [
                ["User Id", "Category Slug"],
                ["100", "network"],
            ]);

            var routed = directory.Route("hardware", new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL, 200UL], routed.StaffIds);
            Assert.DoesNotContain(300UL, routed.StaffIds);
        }

        [Fact]
        public void Route_CapsAtThree()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Active"],
                ["1", "A", "TRUE"],
                ["2", "B", "TRUE"],
                ["3", "C", "TRUE"],
                ["4", "D", "TRUE"],
                ["5", "E", "TRUE"],
            ]);

            Assert.Equal(NpgsqlDirectory.RouteCap, directory.Route(null, DateTimeOffset.UtcNow).StaffIds.Count);
        }

        [Fact]
        public void SheetRows_WithJunk_AreSkippedNotFatal()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Active"],
                ["not-a-number", "Ghost", "TRUE"],
                ["100", "Alice", ""],
                ["", "NoBody", "TRUE"],
            ]);
            directory.ReplaceFromSheet("it_staff_skill",
            [
                ["User Id", "Category Slug"],
                ["100", "does-not-exist"],
                ["100", "network"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["User Id", "From Utc", "Until Utc", "Note"],
                ["100", "not-a-date", "2026-10-07", ""],
                ["100", "2026-10-07", "2026-10-07", "ok"],
            ]);

            var routed = directory.Route("network", new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL], routed.StaffIds);
            Assert.Empty(directory.Route("network", new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)).StaffIds);
        }

        [Fact]
        public void Replace_IgnoresExtraColumns_AndToleratesAnyOrder()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Active"],
                ["100", "Alice", "TRUE"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["Absence Id", "User Id", "Note", "Until Utc", "From Utc"],
                ["1", "100", "out for Oct 7", "2026-10-07", "2026-10-07"],
            ]);

            Assert.Empty(directory.Route(null, new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)).StaffIds);
            Assert.Equal([100UL],
                directory.Route(null, new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)).StaffIds);
        }

        [Fact]
        public void SeedStaffIfEmpty_FillsTheRoster_Once()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.SeedStaffIfEmpty(999, "on-call");
            directory.SeedStaffIfEmpty(888, "other");

            var seeded = directory.Route(null, DateTimeOffset.UtcNow);
            Assert.Contains(999UL, seeded.StaffIds);
            Assert.DoesNotContain(888UL, seeded.StaffIds);
        }
    }
}
