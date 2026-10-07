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
            drop table if exists it_staff_skill, ticket_category, it_staff_absence, it_staff cascade
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
        public void RoundTrips_StaffAndAbsences_FromSheetRows()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Handles", "Active"],
                ["100", "Alice", "wifi, VPN, DNS", "TRUE"],
                ["200", "Bob", "printers, laptops", "TRUE"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["User Id", "From Utc", "Until Utc", "Note"],
                ["200", "2026-10-07", "2026-10-07", "out for Oct 7"],
            ]);

            var available = directory.AvailableStaff(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(
            [
                new StaffMember("100", "Alice", "wifi, VPN, DNS"),
                new StaffMember("200", "Bob", "printers, laptops"),
            ], available);

            var withoutBob = directory.Route(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL], withoutBob.StaffIds);

            var back = directory.Route(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal([100UL, 200UL], back.StaffIds);
        }

        [Fact]
        public void RoundTrips_PriorityGuidance_FromSheetRows()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("priority",
            [
                ["Priority Code", "Description"],
                ["urgent", "ping me immediately"],
                ["no-rush", ""],
            ]);

            var priorities = directory.Priorities();
            Assert.Contains(new PriorityOption("urgent", "ping me immediately"), priorities);
            Assert.Contains(new PriorityOption("no-rush", ""), priorities);
        }

        [Fact]
        public void ReplacePriorities_KeepsCodesThatTicketsStillReference()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            Exec(db, """
                insert into priority (priority_code) values ('report'), ('zz-sacrifice')
                on conflict do nothing
                """);
            Exec(db, """
                insert into discord_user (user_id, username, first_seen_at_utc, last_seen_at_utc)
                values (900, 'req', now(), now()) on conflict do nothing
                """);
            Exec(db, """
                insert into ticket (ticket_id, requester_user_id, priority_code, created_at_utc)
                values ('keepme', 900, 'report', now()) on conflict do nothing
                """);

            directory.ReplaceFromSheet("priority",
            [
                ["Priority Code", "Description"],
                ["urgent", "broken right now"],
            ]);

            var priorities = directory.Priorities();
            var codes = priorities.Select(option => option.Code).ToArray();
            Assert.Contains("urgent", codes);
            Assert.Contains(new PriorityOption("urgent", "broken right now"), priorities);
            Assert.Contains("report", codes);
            Assert.DoesNotContain("zz-sacrifice", codes);
        }

        [Fact]
        public void Route_SkipsInactiveStaff_AndCapsAtThree()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Handles", "Active"],
                ["1", "A", "", "TRUE"],
                ["2", "B", "", "TRUE"],
                ["3", "C", "", "TRUE"],
                ["4", "D", "", "TRUE"],
                ["5", "E", "", "TRUE"],
            ]);

            var routed = directory.Route(DateTimeOffset.UtcNow);
            Assert.Equal(NpgsqlDirectory.RouteCap, routed.StaffIds.Count);
            Assert.Equal([1UL, 2UL, 3UL], routed.StaffIds);
        }

        [Fact]
        public void SheetRows_WithJunk_AreSkippedNotFatal()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["User Id", "Display Name", "Handles", "Active"],
                ["not-a-number", "Ghost", "", "TRUE"],
                ["100", "Alice", "wifi and VPN", ""],
                ["", "NoBody", "", "TRUE"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["User Id", "From Utc", "Until Utc", "Note"],
                ["100", "not-a-date", "2026-10-07", ""],
                ["100", "2026-10-07", "2026-10-07", "ok"],
            ]);

            var available = directory.AvailableStaff(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
            var alice = Assert.Single(available);
            Assert.Equal("Alice", alice.Name);
            Assert.Equal("wifi and VPN", alice.Handles);
            Assert.Empty(directory.Route(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)).StaffIds);
        }

        [Fact]
        public void Replace_IgnoresExtraColumns_AndToleratesAnyOrder()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.ReplaceFromSheet("it_staff",
            [
                ["Active", "Handles", "Display Name", "User Id"],
                ["TRUE", "wifi", "Alice", "100"],
            ]);
            directory.ReplaceFromSheet("it_staff_absence",
            [
                ["Absence Id", "User Id", "Note", "Until Utc", "From Utc"],
                ["1", "100", "out for Oct 7", "2026-10-07", "2026-10-07"],
            ]);

            Assert.Empty(directory.Route(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)).StaffIds);
            Assert.Equal([100UL],
                directory.Route(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)).StaffIds);
        }

        [Fact]
        public void SeedStaffIfEmpty_FillsTheRoster_Once()
        {
            if (Pg is null) return;
            using var db = Connect();
            var directory = Directory(db);

            directory.SeedStaffIfEmpty(999, "on-call");
            directory.SeedStaffIfEmpty(888, "other");

            var seeded = directory.Route(DateTimeOffset.UtcNow);
            Assert.Contains(999UL, seeded.StaffIds);
            Assert.DoesNotContain(888UL, seeded.StaffIds);
            Assert.Contains(directory.AvailableStaff(DateTimeOffset.UtcNow),
                staff => staff.StaffId == "999" && staff.Handles.Length > 0);
        }
    }
}
