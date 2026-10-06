using Npgsql;

namespace Ticket.Adapter.Npgsql;

// One interaction option (a slash-command option, a menu value, a modal field)
// — 1NF: never a concatenated string.
public sealed record InteractionOption(string Name, string? Value);

// One row per Discord interaction the bot receives — every slash command,
// button, select menu, modal and context menu, ticket-related or not.
// user/channel/guild ids are plain snowflakes; the user is a foreign key into
// discord_user (NpgsqlTicketStore's schema), so names are never duplicated.
public sealed record InteractionEntry(
    ulong InteractionId,
    string Kind,
    string Name,
    string? TicketId,
    ulong? UserId,
    string? UserName,
    ulong? ChannelId,
    ulong? GuildId,
    IReadOnlyList<InteractionOption>? Options,
    DateTimeOffset ReceivedAtUtc);

// The audit trail. RecordAsync never throws: a failed audit row must never
// take the bot down.
public sealed class NpgsqlInteractions
{
    private readonly NpgsqlDataSource _db;

    public NpgsqlInteractions(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var cmd = _db.CreateCommand("""
            create table if not exists discord_user (
                user_id            bigint primary key,
                username           text not null default '',
                first_seen_at_utc  timestamptz not null,
                last_seen_at_utc   timestamptz not null
            );

            create table if not exists interaction_kind (
                kind_code  text primary key
            );

            create table if not exists interaction (
                interaction_id   bigint primary key,
                kind_code        text not null references interaction_kind(kind_code),
                name             text not null,
                ticket_id        text,           -- soft reference: the /it interaction
                                                 -- arrives before the ticket row exists
                user_id          bigint references discord_user(user_id),
                channel_id       bigint,
                guild_id         bigint,
                received_at_utc  timestamptz not null
            );
            create index if not exists interaction_received_idx on interaction (received_at_utc desc);

            create table if not exists interaction_option (
                option_id        bigint generated always as identity primary key,
                interaction_id   bigint not null references interaction(interaction_id) on delete cascade,
                option_name      text not null,
                option_value     text
            );
            create index if not exists interaction_option_interaction_idx on interaction_option (interaction_id);

            insert into interaction_kind (kind_code)
                values ('slash'), ('user-command'), ('message-command'),
                       ('component'), ('modal'), ('other')
                on conflict do nothing;
            """);
        cmd.ExecuteNonQuery();
    }

    public async Task RecordAsync(InteractionEntry entry)
    {
        try
        {
            await using var connection = await _db.OpenConnectionAsync().ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync().ConfigureAwait(false);

            if (entry.UserId is { } userId)
            {
                await using (var user = connection.CreateCommand())
                {
                    user.Transaction = tx;
                    user.CommandText = """
                        insert into discord_user (user_id, username, first_seen_at_utc, last_seen_at_utc)
                        values (@id, @name, @at, @at)
                        on conflict (user_id) do update set
                            username = excluded.username, last_seen_at_utc = excluded.last_seen_at_utc
                        """;
                    user.Parameters.AddWithValue("id", (long)userId);
                    user.Parameters.AddWithValue("name", entry.UserName ?? "");
                    user.Parameters.AddWithValue("at", entry.ReceivedAtUtc);
                    await user.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            var inserted = 0;
            await using (var interaction = connection.CreateCommand())
            {
                interaction.Transaction = tx;
                interaction.CommandText = """
                    insert into interaction
                        (interaction_id, kind_code, name, ticket_id, user_id, channel_id, guild_id, received_at_utc)
                    values
                        (@id, @kind, @name, @ticket, @uid, @chan, @guild, @at)
                    on conflict (interaction_id) do nothing
                    """;
                interaction.Parameters.AddWithValue("id", (long)entry.InteractionId);
                interaction.Parameters.AddWithValue("kind", entry.Kind);
                interaction.Parameters.AddWithValue("name", entry.Name);
                interaction.Parameters.AddWithValue("ticket", (object?)entry.TicketId ?? DBNull.Value);
                interaction.Parameters.AddWithValue("uid", (object?)(long?)entry.UserId ?? DBNull.Value);
                interaction.Parameters.AddWithValue("chan", (object?)(long?)entry.ChannelId ?? DBNull.Value);
                interaction.Parameters.AddWithValue("guild", (object?)(long?)entry.GuildId ?? DBNull.Value);
                interaction.Parameters.AddWithValue("at", entry.ReceivedAtUtc);
                inserted = await interaction.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Options only when the interaction itself was new (replays dedupe whole).
            if (inserted == 1 && entry.Options is { Count: > 0 })
                foreach (var option in entry.Options)
                {
                    await using var optionCmd = connection.CreateCommand();
                    optionCmd.Transaction = tx;
                    optionCmd.CommandText = """
                        insert into interaction_option (interaction_id, option_name, option_value)
                        values (@interaction, @name, @value)
                        """;
                    optionCmd.Parameters.AddWithValue("interaction", (long)entry.InteractionId);
                    optionCmd.Parameters.AddWithValue("name", option.Name);
                    optionCmd.Parameters.AddWithValue("value", (object?)option.Value ?? DBNull.Value);
                    await optionCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[postgres] interaction log failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
