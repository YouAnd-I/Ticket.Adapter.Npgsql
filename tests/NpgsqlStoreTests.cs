using System.Text.Json.Nodes;
using Npgsql;
using Ticket.Adapter.Npgsql;
using Ticket.Data;
using Xunit;

namespace Ticket.Adapter.Npgsql.Tests;

// Integration tests against a real Postgres, pointed at by TEST_POSTGRES
// (connection string). Without it they pass as no-ops — run them with:
//   docker run -d --rm -e POSTGRES_PASSWORD=test -p 5433:5432 postgres:17-alpine
//   TEST_POSTGRES="Host=localhost;Port=5433;Username=postgres;Password=test;Database=postgres" dotnet test
public class NpgsqlStoreTests
{
    private static string? Pg => Environment.GetEnvironmentVariable("TEST_POSTGRES");

    private static object? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Pg);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void TicketLifecycle_IsFullyNormalized()
    {
        if (Pg is null) return;

        var store = new NpgsqlTicketStore(Pg);
        var id = Guid.NewGuid().ToString("N")[..8];
        store.Append("<@42>", id, "urgent", auto: true, offline: false,
            "printer on fire", "cant print", "https://cdn/x.png", "<@7>");

        // Ticket row + requester and assignee as users, not strings
        Assert.Equal(1, (long)Scalar($"select count(*) from ticket where ticket_id = '{id}'")!);
        Assert.Equal("<@42>", Scalar(
            $"select '<@' || requester_user_id || '>' from ticket where ticket_id = '{id}'")!);
        Assert.Equal("<@7>", Scalar(
            $"select '<@' || assignee_user_id || '>' from ticket where ticket_id = '{id}'")!);
        Assert.Equal("urgent", Scalar(
            $"select priority_code from ticket where ticket_id = '{id}'")!);

        // Creation is itself the first status event; current status = latest event
        Assert.Equal("open", Scalar(
            $"select status_code from ticket_status_event where ticket_id = '{id}'")!);
        store.AppendStatus("<@7>", id, "planned");
        store.AppendStatus("<@7>", id, "complete");
        Assert.Equal(3, (long)Scalar($"select count(*) from ticket_status_event where ticket_id = '{id}'")!);

        // Notes are rows, counted with count(*), not parsed out of a string
        Assert.Equal(1, store.AppendNote("<@7>", id, "replaced the toner"));
        Assert.Equal(2, store.AppendNote("<@7>", id, "still smoking"));
        Assert.Equal(2, (long)Scalar($"select count(*) from ticket_note where ticket_id = '{id}'")!);

        // Reports are rows; anonymous means no filer at all
        store.AppendReport("<@9>", id, "rude IT", "yelled at me", anonymous: true, null);
        Assert.Equal(1, (long)Scalar(
            $"select count(*) from ticket_report where ticket_id = '{id}' and filed_by_user_id is null")!);

        // LoadJson rebuilds the file store's shape from the normalized rows
        var json = store.LoadJson(id);
        Assert.Equal("complete", json!["status"]!.GetValue<string>());     // latest event
        Assert.Equal("<@42>", json["user"]!.GetValue<string>());          // mention rebuilt
        Assert.Equal(2, ((JsonArray)json["notes"]!).Count);               // ordered notes
        Assert.Equal("urgent", json["priority"]!.GetValue<string>());
        Assert.True(json["auto"]!.GetValue<bool>());
    }

    [Fact]
    public void BestSolution_FindsWordOverlap_AfterSync()
    {
        if (Pg is null) return;

        var dir = Path.Combine(Path.GetTempPath(), "npgsql-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "it-tickets"));
        File.WriteAllText(Path.Combine(dir, "it-tickets", "printer-jam.s.json"),
            """{"Title":"Clear a printer jam","Text":"Open the back and pull the paper","Image":null}""");

        var store = new NpgsqlTicketStore(Pg);
        store.SyncSolutions(dir);

        var solution = store.BestSolution("printer is jammed again");
        Assert.NotNull(solution);
        Assert.Equal("Clear a printer jam", solution!.Title);
        Assert.Equal("Open the back and pull the paper", solution.Text);
    }

    [Fact]
    public void ImportLegacyLog_RebuildsNormalizedRows_ThenSkipsWhenNotEmpty()
    {
        if (Pg is null) return;

        // The import only runs against an empty ticket table; make this test's
        // view of the shared test database empty (FK order matters).
        Scalar("""
            truncate interaction_option, interaction, ticket_report, ticket_note,
                    ticket_status_event, ticket, discord_user, solution restart identity
            """);

        var dir = Path.Combine(Path.GetTempPath(), "npgsql-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "it-tickets.txt"), string.Join('\n',
            "[2026-09-24 13:32:39Z] user=<@42> priority=urgent | I have a cold — ",          // pre-ticket era: skipped
            "[2026-09-24 13:41:44Z] user=<@42> ticket=3a8b5967 priority=urgent | title=I am sad okay | desc=I don't want ice cream",
            "[2026-09-24 13:55:55Z] user=anonymous ticket=3a8b5967 report | complaint=They ate my ice cream | action=And he was rude",
            "[2026-09-24 13:58:25Z] user=<@42> ticket=3a8b5967 status=complete",
            "[2026-09-24 13:28:58Z] user=<@42> ticket=43425012 priority=no-rush auto | title=Don't come | desc=its a test",
            "[2026-09-24 13:28:41Z] user=<@42> ticket=43425012 | note=Did something happen") + "\n");

        var store = new NpgsqlTicketStore(Pg);
        var imported = store.ImportLegacyLog(dir);
        Assert.Equal(2, imported.Tickets);      // both ticket= lines
        Assert.Equal(3, imported.Events);       // 2 opens + 1 complete
        Assert.Equal(1, imported.Notes);
        Assert.Equal(1, imported.Reports);

        // Users normalized from mentions; anonymous report has no filer
        Assert.Equal("<@42>", Scalar("select '<@' || user_id || '>' from discord_user where user_id = 42")!);
        Assert.Equal(1, (long)Scalar(
            "select count(*) from ticket_report where filed_by_user_id is null and complaint = 'They ate my ice cream'")!);
        Assert.Equal("I am sad okay", Scalar(
            "select title from ticket where ticket_id = '3a8b5967'")!);

        // Status timeline ordered by time, not insertion
        Assert.Equal("complete", Scalar("""
            select status_code from ticket_status_event
            where ticket_id = '3a8b5967'
            order by occurred_at_utc desc limit 1
            """)!);

        // A second import must be a no-op now that the ticket table has rows.
        Assert.Equal((Tickets: 0, Events: 0, Notes: 0, Reports: 0), store.ImportLegacyLog(dir));
    }

    [Fact]
    public async Task Interactions_StoresEntryWithOptions_AndDedupes()
    {
        if (Pg is null) return;

        var log = new NpgsqlInteractions(Pg);
        var interactionId = Random.Shared.NextInt64(1_000_000, 900_000_000);
        await log.RecordAsync(new InteractionEntry(
            InteractionId: (ulong)interactionId, Kind: "slash", Name: "it", TicketId: null,
            UserId: 42, UserName: "alice", ChannelId: 100, GuildId: 200,
            Options: [new("title", "printer"), new("priority", "auto")],
            ReceivedAtUtc: DateTimeOffset.UtcNow));

        Assert.Equal(1, (long)Scalar(
            $"select count(*) from interaction where interaction_id = {interactionId}")!);
        Assert.Equal(2, (long)Scalar(
            $"select count(*) from interaction_option where interaction_id = {interactionId}")!);
        Assert.Equal("printer", Scalar(
            $"select option_value from interaction_option where interaction_id = {interactionId} and option_name = 'title'")!);

        // The username lives in discord_user, refreshed on every interaction
        Assert.Equal("alice", Scalar("select username from discord_user where user_id = 42")!);
        await log.RecordAsync(new InteractionEntry(
            InteractionId: (ulong)interactionId, Kind: "slash", Name: "it", TicketId: null,
            UserId: 42, UserName: "alice42", ChannelId: 100, GuildId: 200,
            Options: null, ReceivedAtUtc: DateTimeOffset.UtcNow));
        Assert.Equal("alice42", Scalar("select username from discord_user where user_id = 42")!);
        Assert.Equal(1, (long)Scalar(
            $"select count(*) from interaction where interaction_id = {interactionId}")!); // deduped
    }
}
