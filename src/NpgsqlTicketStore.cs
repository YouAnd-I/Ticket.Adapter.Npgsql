using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Ticket.Data;

namespace Ticket.Adapter.Npgsql;

// The ticket feature's state in Postgres (Neon), fully normalized:
//
//   discord_user(user_id PK, username, first_seen_at_utc, last_seen_at_utc)
//   priority(priority_code PK)            -- lookup: urgent | no-rush | report
//   ticket_status(status_code PK)         -- lookup: open | planned | complete | ...
//   ticket(ticket_id PK, requester → discord_user, assignee → discord_user,
//          priority → priority, title, description, attachment_url,
//          classified_automatically, classifier_offline, created_at_utc)
//   ticket_status_event(id PK, ticket → ticket, status → ticket_status,
//          actor → discord_user, occurred_at_utc)      -- current status = latest
//   ticket_note(id PK, ticket → ticket, author → discord_user, note_text, created_at_utc)
//   ticket_report(id PK, ticket → ticket, filed_by → discord_user NULL=anonymous,
//          complaint, action_taken, file_url, filed_at_utc)
//   solution(slug PK, title, body, image_url)          -- mirrors it-tickets/*.s.json
//
// Same ITicketStore contract as the file store in Ticket.System.Frent; writes
// happen inside the tick exactly like the file store's did — the world only
// writes on user interactions, never per tick.
public sealed partial class NpgsqlTicketStore : ITicketStore
{
    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlDirectory _directory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public NpgsqlTicketStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        EnsureSchema();
        _directory = new NpgsqlDirectory(_db);
    }

    [GeneratedRegex(@"^<?@?(\d+)>?$")]
    private static partial Regex UserIdRegex();

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand("""
            create table if not exists discord_user (
                user_id            bigint primary key,
                username           text not null default '',
                first_seen_at_utc  timestamptz not null,
                last_seen_at_utc   timestamptz not null
            );
            comment on table discord_user is 'Every Discord user the bot has interacted with.';

            create table if not exists priority (
                priority_code  text primary key,
                description    text not null default ''
            );

            create table if not exists ticket_status (
                status_code  text primary key,
                description  text not null default ''
            );

            create table if not exists ticket (
                ticket_id                 text primary key,
                requester_user_id         bigint references discord_user(user_id),
                assignee_user_id          bigint references discord_user(user_id),
                title                     text,
                description               text,
                attachment_url            text,
                priority_code             text not null references priority(priority_code),
                classified_automatically  boolean not null default false,
                classifier_offline        boolean not null default false,
                created_at_utc            timestamptz not null
            );
            comment on column ticket.classified_automatically is 'Priority came from the model, not the user.';
            comment on column ticket.classifier_offline is 'The classifier was unreachable; urgent was assumed.';

            create table if not exists ticket_status_event (
                status_event_id  bigint generated always as identity primary key,
                ticket_id        text not null references ticket(ticket_id),
                status_code      text not null references ticket_status(status_code),
                actor_user_id    bigint references discord_user(user_id),
                occurred_at_utc  timestamptz not null
            );
            create index if not exists ticket_status_event_ticket_idx
                on ticket_status_event (ticket_id, occurred_at_utc);
            comment on table ticket_status_event is 'Status timeline; the current status is the latest event.';

            create table if not exists ticket_note (
                note_id         bigint generated always as identity primary key,
                ticket_id       text not null references ticket(ticket_id),
                author_user_id  bigint references discord_user(user_id),
                note_text       text not null,
                created_at_utc  timestamptz not null
            );
            create index if not exists ticket_note_ticket_idx on ticket_note (ticket_id, created_at_utc);

            create table if not exists ticket_report (
                report_id         bigint generated always as identity primary key,
                ticket_id         text not null references ticket(ticket_id),
                filed_by_user_id  bigint references discord_user(user_id),
                complaint         text not null,
                action_taken      text,
                file_url          text,
                filed_at_utc      timestamptz not null
            );
            comment on column ticket_report.filed_by_user_id is 'NULL = filed anonymously.';

            create table if not exists solution (
                slug        text primary key,
                title       text not null,
                body        text,
                image_url   text
            );

            insert into priority (priority_code) values ('urgent'), ('no-rush'), ('report')
                on conflict do nothing;
            insert into ticket_status (status_code)
                values ('open'), ('planned'), ('complete'), ('reopened'), ('unsolved')
                on conflict do nothing;
            """);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TicketCategory> Categories() => _directory.Categories();

    public TicketRoute Route(string? categorySlug, DateTimeOffset nowUtc) =>
        _directory.Route(categorySlug, nowUtc);

    public void Append(string user, string id, string priority, bool auto, bool offline,
        string? title, string? desc, string? file, string? assigned)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();

        var requester = UpsertUser(connection, tx, user, now);
        var assignee = UpsertUser(connection, tx, assigned, now);
        EnsurePriority(connection, tx, priority);

        using (var ticket = connection.CreateCommand())
        {
            ticket.Transaction = tx;
            ticket.CommandText = """
                insert into ticket
                    (ticket_id, requester_user_id, assignee_user_id, title, description,
                     attachment_url, priority_code, classified_automatically, classifier_offline,
                     created_at_utc)
                values
                    (@id, @requester, @assignee, @title, @desc, @file, @priority, @auto, @offline, @at)
                """;
            ticket.Parameters.AddWithValue("id", id);
            ticket.Parameters.AddWithValue("requester", (object?)(long?)requester ?? DBNull.Value);
            ticket.Parameters.AddWithValue("assignee", (object?)(long?)assignee ?? DBNull.Value);
            ticket.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
            ticket.Parameters.AddWithValue("desc", (object?)desc ?? DBNull.Value);
            ticket.Parameters.AddWithValue("file", (object?)file ?? DBNull.Value);
            ticket.Parameters.AddWithValue("priority", priority);
            ticket.Parameters.AddWithValue("auto", auto);
            ticket.Parameters.AddWithValue("offline", offline);
            ticket.Parameters.AddWithValue("at", now);
            ticket.ExecuteNonQuery();
        }

        InsertStatusEvent(connection, tx, id, "open", requester, now);

        tx.Commit();
    }

    // The same JSON shape the file store writes, so TicketSystem.View hydrates
    // identically from either backend. The current status is the latest event,
    // the notes are ordered by creation — nothing is stored denormalized.
    public JsonNode? LoadJson(string id)
    {
        using var cmd = _db.CreateCommand("""
            select t.requester_user_id, t.assignee_user_id, t.title, t.description,
                   t.attachment_url, t.priority_code, t.classified_automatically,
                   t.classifier_offline, t.created_at_utc,
                   (select s.status_code from ticket_status_event s
                    where s.ticket_id = t.ticket_id
                    order by s.occurred_at_utc desc, s.status_event_id desc
                    limit 1) as status,
                   coalesce((select jsonb_agg(n.note_text order by n.created_at_utc, n.note_id)
                             from ticket_note n where n.ticket_id = t.ticket_id), '[]'::jsonb) as notes
            from ticket t
            where t.ticket_id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new JsonObject
        {
            ["id"] = id,
            ["user"] = Mention(reader.IsDBNull(0) ? null : (ulong)reader.GetInt64(0)),
            ["created"] = reader.GetFieldValue<DateTimeOffset>(8).UtcDateTime.ToString("u"),
            ["priority"] = reader.GetString(5),
            ["auto"] = reader.GetBoolean(6),
            ["offline"] = reader.GetBoolean(7),
            ["assigned"] = reader.IsDBNull(1) ? null : Mention((ulong)reader.GetInt64(1)),
            ["title"] = reader.IsDBNull(2) ? null : reader.GetString(2),
            ["description"] = reader.IsDBNull(3) ? null : reader.GetString(3),
            ["file"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["status"] = reader.IsDBNull(9) ? "open" : reader.GetString(9),
            ["notes"] = JsonNode.Parse(reader.GetString(10)) as JsonArray ?? [],
        };
    }

    // How-tos stay hand-authored as it-tickets/*.s.json; SyncSolutions mirrors
    // them into Postgres on boot, so this reads the mirror.
    public TicketSolution? BestSolution(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return null;

        List<TicketSolution> all = [];
        using (var cmd = _db.CreateCommand("select title, body, image_url from solution"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                all.Add(new(reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));

        return all
            .Select(s => (s, score: words.Count(w =>
                s.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                (s.Text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.s)
            .FirstOrDefault();
    }

    public void AppendStatus(string user, string id, string status)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        EnsureStatus(connection, tx, status);
        InsertStatusEvent(connection, tx, id, status, UpsertUser(connection, tx, user, now), now);
        tx.Commit();
    }

    public int AppendNote(string user, string id, string note)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                insert into ticket_note (ticket_id, author_user_id, note_text, created_at_utc)
                values (@id, @author, @note, @at)
                """;
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("author",
                (object?)(long?)UpsertUser(connection, tx, user, now) ?? DBNull.Value);
            insert.Parameters.AddWithValue("note", note);
            insert.Parameters.AddWithValue("at", now);
            insert.ExecuteNonQuery();
        }

        int count;
        using (var tally = connection.CreateCommand())
        {
            tally.Transaction = tx;
            tally.CommandText = "select count(*) from ticket_note where ticket_id = @id";
            tally.Parameters.AddWithValue("id", id);
            count = Convert.ToInt32(tally.ExecuteScalar());
        }

        tx.Commit();
        return count;
    }

    public void AppendReport(string user, string id, string complaint, string action,
        bool anonymous, string? file)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            insert into ticket_report (ticket_id, filed_by_user_id, complaint, action_taken, file_url, filed_at_utc)
            values (@id, @by, @complaint, @action, @file, @at)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("by",
            (object?)(long?)(anonymous ? null : UpsertUser(connection, tx, user, now)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("complaint", complaint);
        cmd.Parameters.AddWithValue("action", (object?)action ?? DBNull.Value);
        cmd.Parameters.AddWithValue("file", (object?)file ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", now);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // One-time import of the file store's it-tickets.txt. The log line formats
    // are the file store's:
    //   [t] user=U ticket=ID priority=P[ auto][ classifier-offline] | title=T | desc=D[| file=F][| assigned=A]
    //   [t] user=U ticket=ID status=S
    //   [t] user=U ticket=ID | note=N
    //   [t] user=[anonymous|U] ticket=ID report | complaint=C | action=A[ | file=F]
    // Lines without a ticket= belong to the pre-ticket era and are skipped.
    // Only runs while the ticket table is empty, so restarts never double-import.
    public (int Tickets, int Events, int Notes, int Reports) ImportLegacyLog(string baseDir)
    {
        using (var probe = _db.CreateCommand("select exists (select 1 from ticket)"))
            if ((bool)probe.ExecuteScalar()!) return (0, 0, 0, 0);

        int tickets = 0, events = 0, notes = 0, reports = 0;
        var logPath = Path.Combine(baseDir, "it-tickets.txt");
        if (!File.Exists(logPath)) return (0, 0, 0, 0);

        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        var seen = new HashSet<string>();

        foreach (var raw in File.ReadLines(logPath))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('[')) continue;
            var close = line.IndexOf(']');
            if (close <= 1 || !DateTimeOffset.TryParse(line[1..close], out var at)) continue;
            var body = line[(close + 1)..].Trim();
            if (!body.Contains("ticket=")) continue;

            var user = Field(body, "user=");
            var id = Field(body, "ticket=");

            try
            {
                if (body.Contains(" | note="))
                {
                    if (id is null || !seen.Contains(id)) continue;
                    var note = Part(body, "note=");
                    if (note is null) continue;
                    Exec(connection, tx, """
                        insert into ticket_note (ticket_id, author_user_id, note_text, created_at_utc)
                        values (@id, @author, @note, @at)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        cmd.Parameters.AddWithValue("author", (object?)(long?)UpsertUser(connection, tx, user, at) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("note", note);
                        cmd.Parameters.AddWithValue("at", at);
                    });
                    notes++;
                }
                else if (body.Contains(" report | "))
                {
                    if (id is null || !seen.Contains(id)) continue;
                    var complaint = Part(body, "complaint=");
                    var actionTaken = Part(body, "action=");
                    var file = Field(body, "file=");
                    if (complaint is null) continue;
                    Exec(connection, tx, """
                        insert into ticket_report (ticket_id, filed_by_user_id, complaint, action_taken, file_url, filed_at_utc)
                        values (@id, @by, @complaint, @action, @file, @at)
                        """, cmd =>
                    {
                        var by = user is "anonymous" ? null : UpsertUser(connection, tx, user, at);
                        cmd.Parameters.AddWithValue("id", id);
                        cmd.Parameters.AddWithValue("by", (object?)(long?)by ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("complaint", complaint);
                        cmd.Parameters.AddWithValue("action", (object?)actionTaken ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("file", (object?)file ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("at", at);
                    });
                    reports++;
                }
                else if (body.Contains("status="))
                {
                    if (id is null || !seen.Contains(id)) continue;
                    var status = Field(body, "status=");
                    if (status is null) continue;
                    EnsureStatus(connection, tx, status);
                    Exec(connection, tx, """
                        insert into ticket_status_event (ticket_id, status_code, actor_user_id, occurred_at_utc)
                        values (@id, @status, @actor, @at)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        cmd.Parameters.AddWithValue("status", status);
                        cmd.Parameters.AddWithValue("actor", (object?)(long?)UpsertUser(connection, tx, user, at) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("at", at);
                    });
                    events++;
                }
                else if (body.Contains(" | title=") && id is not null && seen.Add(id))
                {
                    var priority = Field(body, "priority=") ?? "urgent";
                    var auto = body.Contains(" auto");
                    var offline = body.Contains(" classifier-offline");
                    var title = Part(body, "title=") is { } t && t != "(no title)" ? t : null;
                    var desc = Part(body, "desc=");
                    var file = Field(body, "file=");
                    var assigned = Field(body, "assigned=");
                    EnsurePriority(connection, tx, priority);

                    var requester = UpsertUser(connection, tx, user, at);
                    var assignee = UpsertUser(connection, tx, assigned, at);
                    Exec(connection, tx, """
                        insert into ticket
                            (ticket_id, requester_user_id, assignee_user_id, title, description,
                             attachment_url, priority_code, classified_automatically, classifier_offline,
                             created_at_utc)
                        values
                            (@id, @requester, @assignee, @title, @desc, @file, @priority, @auto, @offline, @at)
                        on conflict (ticket_id) do nothing
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        cmd.Parameters.AddWithValue("requester", (object?)(long?)requester ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("assignee", (object?)(long?)assignee ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("desc", (object?)desc ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("file", (object?)file ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("priority", priority);
                        cmd.Parameters.AddWithValue("auto", auto);
                        cmd.Parameters.AddWithValue("offline", offline);
                        cmd.Parameters.AddWithValue("at", at);
                    });
                    Exec(connection, tx, """
                        insert into ticket_status_event (ticket_id, status_code, actor_user_id, occurred_at_utc)
                        values (@id, 'open', @actor, @at)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        cmd.Parameters.AddWithValue("actor", (object?)(long?)requester ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("at", at);
                    });
                    tickets++;
                    events++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[postgres] skipped log line: {ex.Message}: {line}");
            }
        }

        tx.Commit();
        Console.WriteLine(
            $"[postgres] imported {tickets} tickets, {events} status events, {notes} notes, {reports} reports from {logPath}");
        return (tickets, events, notes, reports);
    }

    // How-tos stay hand-authored as it-tickets/*.s.json; this mirrors them into
    // Postgres on every boot (files win, so edits flow through).
    public int SyncSolutions(string baseDir)
    {
        var dirPath = Path.Combine(baseDir, "it-tickets");
        if (!Directory.Exists(dirPath)) return 0;

        int count = 0;
        foreach (var path in Directory.EnumerateFiles(dirPath, "*.s.json"))
        {
            try
            {
                var solution = JsonSerializer.Deserialize<TicketSolution>(
                    File.ReadAllText(path), JsonOpts);
                if (solution is null) continue;
                var slug = Path.GetFileName(path)[..^".s.json".Length];

                using var cmd = _db.CreateCommand("""
                    insert into solution (slug, title, body, image_url)
                    values (@slug, @title, @body, @image)
                    on conflict (slug) do update set
                        title = excluded.title, body = excluded.body, image_url = excluded.image_url
                    """);
                cmd.Parameters.AddWithValue("slug", slug);
                cmd.Parameters.AddWithValue("title", solution.Title);
                cmd.Parameters.AddWithValue("body", (object?)solution.Text ?? DBNull.Value);
                cmd.Parameters.AddWithValue("image", (object?)solution.Image ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                count++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[postgres] skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return count;
    }

    // ---- helpers ------------------------------------------------------------

    private static void Exec(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, Action<NpgsqlCommand> bind)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void InsertStatusEvent(NpgsqlConnection connection, NpgsqlTransaction tx,
        string ticketId, string status, ulong? actor, DateTimeOffset at) =>
        Exec(connection, tx, """
            insert into ticket_status_event (ticket_id, status_code, actor_user_id, occurred_at_utc)
            values (@id, @status, @actor, @at)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("id", ticketId);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("actor", (object?)(long?)actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("at", at);
        });

    private void EnsurePriority(NpgsqlConnection connection, NpgsqlTransaction tx, string code) =>
        Exec(connection, tx,
            "insert into priority (priority_code) values (@code) on conflict do nothing",
            cmd => cmd.Parameters.AddWithValue("code", code));

    private void EnsureStatus(NpgsqlConnection connection, NpgsqlTransaction tx, string code) =>
        Exec(connection, tx,
            "insert into ticket_status (status_code) values (@code) on conflict do nothing",
            cmd => cmd.Parameters.AddWithValue("code", code));

    // The adapter passes Discord mentions ("<@42>"); the store normalizes them
    // to the user table. Unknown mention → NULL, with the raw text logged.
    private ulong? UpsertUser(NpgsqlConnection connection, NpgsqlTransaction tx,
        string? user, DateTimeOffset at)
    {
        var id = ParseUserId(user);
        if (id is null)
        {
            if (!string.IsNullOrWhiteSpace(user) && user is not "anonymous")
                Console.WriteLine($"[postgres] could not parse user id from '{user}'");
            return null;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            insert into discord_user (user_id, username, first_seen_at_utc, last_seen_at_utc)
            values (@id, '', @at, @at)
            on conflict (user_id) do update set last_seen_at_utc = excluded.last_seen_at_utc
            """;
        cmd.Parameters.AddWithValue("id", (long)id.Value);
        cmd.Parameters.AddWithValue("at", at);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static ulong? ParseUserId(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        var match = UserIdRegex().Match(user);
        return match.Success ? ulong.Parse(match.Groups[1].Value) : null;
    }

    private static string? Mention(ulong? userId) => userId is null ? null : $"<@{userId}>";

    // "key=value" at the start of a space-separated segment.
    private static string? Field(string body, string key)
    {
        var segment = body.Split(' ').FirstOrDefault(p => p.StartsWith(key));
        return segment is null ? null : segment[key.Length..];
    }

    // The text after "key=" up to the next " | " segment, null if absent.
    private static string? Part(string body, string key)
    {
        var segment = body.Split(" | ").FirstOrDefault(p => p.StartsWith(key));
        return segment is null ? null : segment[key.Length..];
    }
}
