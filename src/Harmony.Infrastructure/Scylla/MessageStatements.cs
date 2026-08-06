using Cassandra;

namespace Harmony.Infrastructure.Scylla;

public class MessageStatements
{
    public PreparedStatement InsertByChannel { get; }
    public PreparedStatement InsertById { get; }
    public PreparedStatement SelectByChannel { get; }
    public PreparedStatement SelectByChannelBefore { get; }
    public PreparedStatement SelectByChannelAtOrAfter { get; }
    public PreparedStatement SelectByChannelAfter { get; }
    public PreparedStatement SelectById { get; }
    public PreparedStatement SoftDeleteByChannel { get; }
    public PreparedStatement SoftDeleteById { get; }
    public PreparedStatement EditByChannel { get; }
    public PreparedStatement EditById { get; }
    public PreparedStatement InsertPinned { get; }
    public PreparedStatement DeletePinned { get; }
    public PreparedStatement SelectPinned { get; }
    public PreparedStatement PurgeChannelMessages { get; } // Added!
    public PreparedStatement PurgeChannelPins { get; } // Added!

    public MessageStatements(IScyllaSessionFactory factory)
    {
        var session = factory.Session;
        var ks = factory.Keyspace;

        InsertByChannel = session.Prepare(
            $@"INSERT INTO {ks}.messages_by_channel
                (channel_id, message_id, user_id, content, attachment_ids,
                 mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type,
                 forward_snapshot)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
        );

        InsertById = session.Prepare(
            $@"INSERT INTO {ks}.messages_by_id
                (message_id, channel_id, user_id, content, attachment_ids,
                 mention_ids, reply_to_id, is_deleted, is_edited, edited_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
        );

        SelectByChannel = session.Prepare(
            $@"SELECT channel_id, message_id, user_id, content, attachment_ids,
                      mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type,
                      forward_snapshot
               FROM {ks}.messages_by_channel
               WHERE channel_id = ?
               LIMIT ?"
        );

        SelectByChannelBefore = session.Prepare(
            $@"SELECT channel_id, message_id, user_id, content, attachment_ids,
                      mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type,
                      forward_snapshot
               FROM {ks}.messages_by_channel
               WHERE channel_id = ? AND message_id < ?
               LIMIT ?"
        );

        // Newer-or-equal half of an "around" window. Clustering order is message_id DESC, so we
        // reverse to ASC to walk forward from the target; the repo flips the result back to DESC.
        SelectByChannelAtOrAfter = session.Prepare(
            $@"SELECT channel_id, message_id, user_id, content, attachment_ids,
                      mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type,
                      forward_snapshot
               FROM {ks}.messages_by_channel
               WHERE channel_id = ? AND message_id >= ?
               ORDER BY message_id ASC
               LIMIT ?"
        );

        // Strictly-newer page (scroll-down "load newer" after a jump). Same ASC reversal.
        SelectByChannelAfter = session.Prepare(
            $@"SELECT channel_id, message_id, user_id, content, attachment_ids,
                      mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type,
                      forward_snapshot
               FROM {ks}.messages_by_channel
               WHERE channel_id = ? AND message_id > ?
               ORDER BY message_id ASC
               LIMIT ?"
        );

        SelectById = session.Prepare(
            $@"SELECT message_id, channel_id, user_id, content, attachment_ids,
                      mention_ids, reply_to_id, is_deleted, is_edited, edited_at
               FROM {ks}.messages_by_id
               WHERE message_id = ?"
        );

        SoftDeleteByChannel = session.Prepare(
            $@"UPDATE {ks}.messages_by_channel
               SET is_deleted = true
               WHERE channel_id = ? AND message_id = ?"
        );

        SoftDeleteById = session.Prepare(
            $@"UPDATE {ks}.messages_by_id
               SET is_deleted = true
               WHERE message_id = ?"
        );

        EditByChannel = session.Prepare(
            $@"UPDATE {ks}.messages_by_channel
               SET content = ?, mention_ids = ?, is_edited = true, edited_at = ?
               WHERE channel_id = ? AND message_id = ?"
        );

        EditById = session.Prepare(
            $@"UPDATE {ks}.messages_by_id
               SET content = ?, mention_ids = ?, is_edited = true, edited_at = ?
               WHERE message_id = ?"
        );

        InsertPinned = session.Prepare(
            $@"INSERT INTO {ks}.pinned_messages (channel_id, pinned_at, message_id, pinned_by)
               VALUES (?, ?, ?, ?)"
        );

        DeletePinned = session.Prepare(
            $@"DELETE FROM {ks}.pinned_messages
               WHERE channel_id = ? AND pinned_at = ?"
        );

        SelectPinned = session.Prepare(
            $@"SELECT channel_id, pinned_at, message_id, pinned_by
               FROM {ks}.pinned_messages
               WHERE channel_id = ?"
        );

        // Compile high-speed partition delete queries on boot [14]
        PurgeChannelMessages = session.Prepare(
            $"DELETE FROM {ks}.messages_by_channel WHERE channel_id = ?"
        );

        PurgeChannelPins = session.Prepare(
            $"DELETE FROM {ks}.pinned_messages WHERE channel_id = ?"
        );
    }
}
