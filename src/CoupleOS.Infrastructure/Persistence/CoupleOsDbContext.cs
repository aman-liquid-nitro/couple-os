using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class CoupleOsDbContext(DbContextOptions<CoupleOsDbContext> options) : DbContext(options)
{
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();

    /// <summary>
    /// Tasks, reminders and commitments — one table discriminated by
    /// <c>kind</c> (ARCHITECTURE.md §3), so one DbSet serves three of the seven
    /// tools. Named for the table rather than for the entity, which is called
    /// <see cref="TaskItem"/> for the reason recorded there.
    /// </summary>
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<DumpFile> DumpFiles => Set<DumpFile>();
    public DbSet<DumpRun> DumpRuns => Set<DumpRun>();
    public DbSet<DumpBlock> DumpBlocks => Set<DumpBlock>();
    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Memory>(e =>
        {
            e.ToTable("memories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.Visibility).HasColumnName("visibility");
        });

        b.Entity<ShoppingItem>(e =>
        {
            e.ToTable("shopping_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.NormalizedName).HasColumnName("normalized_name");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.AddedBy).HasColumnName("added_by");

            // status, recurring, created_at and the rest keep their database
            // defaults. Mapping a column just to restate its default invites the
            // two definitions to drift.
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.DueAt).HasColumnName("due_at");
            e.Property(x => x.CommittedToUserId).HasColumnName("committed_to_user_id");

            // status, source, created_at, updated_at and the rest keep their
            // database defaults, as shopping_items does. status is the one worth
            // naming: every task starts 'todo', and mapping the column would let
            // a future entity default disagree with the schema about that.
        });

        b.Entity<AiAction>(e =>
        {
            e.ToTable("ai_actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ToolName).HasColumnName("tool_name");
            e.Property(x => x.Arguments).HasColumnName("arguments").HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");

            // Model attribution. These columns existed in data/schema.sql from the
            // first commit and nothing wrote them, which is worse than their being
            // absent: an empty column reads as "this run had no model" rather than
            // "nobody implemented this".
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.LlmRole).HasColumnName("llm_role");
            e.Property(x => x.PromptTokens).HasColumnName("prompt_tokens");
            e.Property(x => x.CompletionTokens).HasColumnName("completion_tokens");
        });

        b.Entity<DumpFile>(e =>
        {
            e.ToTable("dump_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.ContentVersion).HasColumnName("content_version");
            e.Property(x => x.LastProcessedAt).HasColumnName("last_processed_at");
        });

        b.Entity<DumpRun>(e =>
        {
            e.ToTable("dump_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.DumpFileId).HasColumnName("dump_file_id");
            e.Property(x => x.TriggeredBy).HasColumnName("triggered_by");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.BlocksSeen).HasColumnName("blocks_seen");
            e.Property(x => x.BlocksProcessed).HasColumnName("blocks_processed");
            e.Property(x => x.BlocksNeedingInput).HasColumnName("blocks_needing_input");
            e.Property(x => x.BlocksFailed).HasColumnName("blocks_failed");
            e.Property(x => x.EntitiesCreated).HasColumnName("entities_created");
            e.Property(x => x.EntitiesUpdated).HasColumnName("entities_updated");

            // jsonb stated explicitly. Without it the string maps to text and
            // Postgres refuses the insert rather than casting, which is the
            // right refusal found at the wrong time.
            e.Property(x => x.Report).HasColumnName("report").HasColumnType("jsonb");
        });

        b.Entity<DumpBlock>(e =>
        {
            e.ToTable("dump_blocks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DumpFileId).HasColumnName("dump_file_id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.RawText).HasColumnName("raw_text");
            e.Property(x => x.LineStart).HasColumnName("line_start");
            e.Property(x => x.LineEnd).HasColumnName("line_end");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.Question).HasColumnName("question");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        b.Entity<ConversationSession>(e =>
        {
            e.ToTable("conversation_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
        });

        b.Entity<ConversationMessage>(e =>
        {
            e.ToTable("conversation_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Role).HasColumnName("role");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // Declared for the insert ordering, not for navigation. EF Core
            // orders inserts by the relationships in the model rather than by the
            // database's foreign keys, so a session and its first message saved
            // together get an arbitrary order and fail the FK about half the time
            // — the same defect that put couple_members before couples during M1.
            // No navigation property is needed here, or wanted: a thread is read
            // as a list of messages, and an include would invite loading the
            // whole transcript to append one line to it.
            e.HasOne<ConversationSession>()
                .WithMany()
                .HasForeignKey(x => x.SessionId);
        });

        // NO global query filter on couple_id or visibility, and that is a
        // decision rather than an omission. ADR 0005 makes the database the
        // enforcement boundary. A query filter here would silently satisfy
        // every test whether or not the policy works, so RLS could rot
        // undetected until the first FromSql or projection bypassed EF.
        //
        // Application-level filtering returns as ADR 0005's "second line" once
        // these tests prove the first line holds — never before.
    }
}
