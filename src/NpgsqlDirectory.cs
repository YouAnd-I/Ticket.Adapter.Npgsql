using System.Globalization;
using Npgsql;
using Ticket.Data;

namespace Ticket.Adapter.Npgsql;

public sealed class NpgsqlDirectory
{
    public const int RouteCap = 3;

    public static readonly IReadOnlyList<string> EditableTabs =
        ["ticket_category", "it_staff", "it_staff_skill", "it_staff_absence"];

    private readonly NpgsqlDataSource _db;

    public NpgsqlDirectory(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString))
    {
    }

    public NpgsqlDirectory(NpgsqlDataSource db)
    {
        _db = db;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand("""
            create table if not exists ticket_category (
                category_slug  text primary key,
                description    text not null default '',
                priority_code  text
            );

            create table if not exists it_staff (
                user_id       bigint primary key references discord_user(user_id),
                display_name  text not null default '',
                active        boolean not null default true
            );

            create table if not exists it_staff_skill (
                user_id       bigint not null references it_staff(user_id) on delete cascade,
                category_slug text not null references ticket_category(category_slug) on delete cascade,
                primary key (user_id, category_slug)
            );

            create table if not exists it_staff_absence (
                absence_id  bigint generated always as identity primary key,
                user_id     bigint not null references it_staff(user_id) on delete cascade,
                from_utc    timestamptz not null,
                until_utc   timestamptz not null,
                note        text not null default ''
            );

            insert into ticket_category (category_slug, description) values
                ('network', 'wifi, VPN, DNS, internet connectivity outages'),
                ('hardware', 'printers, laptops, peripherals, physical machines'),
                ('account-access', 'logins, passwords, permissions, lockouts'),
                ('software', 'apps, licenses, updates, configuration')
            on conflict do nothing;
            """);
        cmd.ExecuteNonQuery();
    }

    public void SeedStaffIfEmpty(ulong userId, string name)
    {
        using var probe = _db.CreateCommand("select not exists (select 1 from it_staff)");
        if (!(bool)probe.ExecuteScalar()!) return;

        using var cmd = _db.CreateCommand("""
            insert into discord_user (user_id, username, first_seen_at_utc, last_seen_at_utc)
            values (@id, @name, now(), now())
            on conflict (user_id) do update set username = excluded.username;

            insert into it_staff (user_id, display_name, active)
            values (@id, @name, true)
            on conflict (user_id) do nothing;
            """);
        cmd.Parameters.AddWithValue("id", (long)userId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TicketCategory> Categories()
    {
        var categories = new List<TicketCategory>();
        using var cmd = _db.CreateCommand(
            "select category_slug, description from ticket_category order by category_slug");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            categories.Add(new(reader.GetString(0), reader.GetString(1)));
        return categories;
    }

    public TicketRoute Route(string? categorySlug, DateTimeOffset nowUtc)
    {
        var staff = new List<ulong>();
        using var cmd = _db.CreateCommand("""
            with candidate as (
                select s.user_id,
                       exists (select 1 from it_staff_skill k
                               where k.user_id = s.user_id and k.category_slug = @slug) as specialist
                from it_staff s
                where s.active
                  and not exists (select 1 from it_staff_absence a
                                  where a.user_id = s.user_id
                                    and @now between a.from_utc and a.until_utc)
            )
            select user_id from candidate
            where @slug is null
               or specialist
               or not exists (select 1 from candidate c2 where c2.specialist)
            order by specialist desc, user_id
            limit @cap
            """);
        cmd.Parameters.AddWithValue("slug", (object?)categorySlug ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", nowUtc);
        cmd.Parameters.AddWithValue("cap", RouteCap);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            staff.Add((ulong)reader.GetInt64(0));
        return new TicketRoute(staff);
    }

    public void ReplaceFromSheet(string tab, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        if (!EditableTabs.Contains(tab)) return;
        if (rows.Count < 2) return;

        var columns = HeaderMap(rows[0]);
        using var connection = _db.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        var data = rows.Skip(1).ToList();

        if (tab == "ticket_category") ReplaceCategories(connection, tx, data, columns);
        if (tab == "it_staff") ReplaceStaff(connection, tx, data, columns);
        if (tab == "it_staff_skill") ReplaceSkills(connection, tx, data, columns);
        if (tab == "it_staff_absence") ReplaceAbsences(connection, tx, data, columns);

        tx.Commit();
    }

    private static Dictionary<string, int> HeaderMap(IReadOnlyList<string?> header)
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i];
            if (name is null) continue;
            map[HeaderKey(name)] = i;
        }
        return map;
    }

    private static string HeaderKey(string header) =>
        header.Replace(" ", "").Replace("_", "").ToLowerInvariant();

    private static string? Cell(IReadOnlyList<string?> row, Dictionary<string, int> columns, string name) =>
        columns.TryGetValue(HeaderKey(name), out var index) && index < row.Count
            ? Blank(row[index])
            : null;

    private static void ReplaceCategories(NpgsqlConnection connection, NpgsqlTransaction tx,
        List<IReadOnlyList<string?>> rows, Dictionary<string, int> columns)
    {
        Exec(connection, tx, "delete from ticket_category");
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var slug = Cell(row, columns, "category_slug");
            if (slug is null || !seen.Add(slug)) continue;
            Exec(connection, tx, """
                insert into ticket_category (category_slug, description, priority_code)
                values (@slug, @desc, @priority)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("slug", slug);
                cmd.Parameters.AddWithValue("desc", Cell(row, columns, "description") ?? "");
                cmd.Parameters.AddWithValue("priority",
                    (object?)Cell(row, columns, "priority_code") ?? DBNull.Value);
            });
        }
    }

    private static void ReplaceStaff(NpgsqlConnection connection, NpgsqlTransaction tx,
        List<IReadOnlyList<string?>> rows, Dictionary<string, int> columns)
    {
        Exec(connection, tx, "delete from it_staff");
        var seen = new HashSet<ulong>();
        foreach (var row in rows)
        {
            if (!ulong.TryParse(Cell(row, columns, "user_id"), out var userId) || !seen.Add(userId)) continue;
            var name = Cell(row, columns, "display_name") ?? "";
            var active = Cell(row, columns, "active") is not { Length: > 0 } inactive
                || inactive.Trim().ToUpperInvariant() != "FALSE";
            Exec(connection, tx, """
                insert into discord_user (user_id, username, first_seen_at_utc, last_seen_at_utc)
                values (@id, @name, now(), now())
                on conflict (user_id) do update set username = excluded.username
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", (long)userId);
                cmd.Parameters.AddWithValue("name", name);
            });
            Exec(connection, tx, """
                insert into it_staff (user_id, display_name, active)
                values (@id, @name, @active)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", (long)userId);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("active", active);
            });
        }
    }

    private static void ReplaceSkills(NpgsqlConnection connection, NpgsqlTransaction tx,
        List<IReadOnlyList<string?>> rows, Dictionary<string, int> columns)
    {
        Exec(connection, tx, "delete from it_staff_skill");
        var staff = Known(connection, tx, "select user_id from it_staff", reader => reader.GetInt64(0));
        var categories = Known(connection, tx, "select category_slug from ticket_category", reader => reader.GetString(0));
        var seen = new HashSet<(long, string)>();
        foreach (var row in rows)
        {
            if (!long.TryParse(Cell(row, columns, "user_id"), out var userId)) continue;
            var slug = Cell(row, columns, "category_slug");
            if (slug is null || !staff.Contains(userId) || !categories.Contains(slug)
                || !seen.Add((userId, slug))) continue;
            Exec(connection, tx, """
                insert into it_staff_skill (user_id, category_slug)
                values (@id, @slug)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", userId);
                cmd.Parameters.AddWithValue("slug", slug);
            });
        }
    }

    private static void ReplaceAbsences(NpgsqlConnection connection, NpgsqlTransaction tx,
        List<IReadOnlyList<string?>> rows, Dictionary<string, int> columns)
    {
        Exec(connection, tx, "delete from it_staff_absence");
        var staff = Known(connection, tx, "select user_id from it_staff", reader => reader.GetInt64(0));
        foreach (var row in rows)
        {
            if (!long.TryParse(Cell(row, columns, "user_id"), out var userId) || !staff.Contains(userId)) continue;
            if (!TryWindow(Cell(row, columns, "from_utc"), Cell(row, columns, "until_utc"),
                    out var from, out var until)) continue;
            Exec(connection, tx, """
                insert into it_staff_absence (user_id, from_utc, until_utc, note)
                values (@id, @from, @until, @note)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("id", userId);
                cmd.Parameters.AddWithValue("from", from);
                cmd.Parameters.AddWithValue("until", until);
                cmd.Parameters.AddWithValue("note", Cell(row, columns, "note") ?? "");
            });
        }
    }

    private static HashSet<T> Known<T>(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, Func<NpgsqlDataReader, T> read)
    {
        var values = new HashSet<T>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            values.Add(read(reader));
        return values;
    }

    internal static bool TryWindow(string? fromText, string? untilText,
        out DateTimeOffset from, out DateTimeOffset until)
    {
        from = default;
        until = default;
        if (!TryMoment(fromText, false, out from)) return false;
        if (!TryMoment(untilText, true, out until)) return false;
        return until > from;
    }

    private static bool TryMoment(string? text, bool endOfDay, out DateTimeOffset moment)
    {
        moment = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var formats = text.Length == 10
            ? new[] { "yyyy-MM-dd" }
            : ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd H:mm:ss"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out moment)) return false;
        if (text.Length == 10 && endOfDay)
            moment = moment.UtcDateTime.AddDays(1).AddSeconds(-1);
        return true;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Exec(NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, Action<NpgsqlCommand>? bind = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }
}
