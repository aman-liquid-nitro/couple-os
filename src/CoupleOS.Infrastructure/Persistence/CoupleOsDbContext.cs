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
    /// <summary>The calendar. One entity, named CalendarEvent because `event` is a keyword.</summary>
    public DbSet<CalendarEvent> Events => Set<CalendarEvent>();

    public DbSet<Expense> Expenses => Set<Expense>();

    /// <summary>Read-only. Twelve system rows are seeded; nothing in V0 writes them.</summary>
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();

    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<DumpFile> DumpFiles => Set<DumpFile>();
    public DbSet<DumpRun> DumpRuns => Set<DumpRun>();
    public DbSet<DumpBlock> DumpBlocks => Set<DumpBlock>();
    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AttachmentLink> AttachmentLinks => Set<AttachmentLink>();

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
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.Assertion).HasColumnName("assertion");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.SubjectKey).HasColumnName("subject_key");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.SupersededById).HasColumnName("superseded_by_id");

            // Stated, for the reason expenses.amount is: numeric(3,2) is a
            // two-decimal fraction of one, and EF's default decimal mapping is
            // not that. A confidence silently rounded is ADR 0006's cap made
            // meaningless.
            e.Property(x => x.Confidence).HasColumnName("confidence").HasColumnType("numeric(3,2)");

            // Read, never written. The column's default is now(), and mapping it
            // without this would have EF insert the CLR's DateTimeOffset.MinValue
            // — year 1, which sorts first forever and would put every new memory
            // at the bottom of a recency ranking.
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            // Declared for the insert ordering, not for navigation — the same
            // reason conversation_messages declares one, and the third time this
            // codebase has been bitten by it. EF Core orders writes by the
            // relationships in the model rather than by the database's foreign
            // keys, so a supersession saved in one SaveChanges put the UPDATE that
            // points at the new memory *before* the INSERT that creates it, and
            // memories_superseded_by_id_fkey refused every correction. A
            // self-reference with no navigation property is the whole fix.
            e.HasOne<Memory>()
                .WithMany()
                .HasForeignKey(x => x.SupersededById);

            // importance keeps its database default and is deliberately unmapped;
            // embedding, source_message_id, confirmed_at and
            // visibility_changed_at wait on flows V0 does not have. search_tsv is
            // GENERATED ALWAYS and mapping it would make every insert fail.
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
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

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

            // Mapped now, and it was the omission debt 39 records: the column
            // defaults to 'chat', so every task typed into shared.md claimed the
            // provenance of a conversation.
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            // status, updated_at and the rest keep their database defaults.
            // status is the one worth naming: every task starts 'todo', and
            // mapping the column would let a future entity default disagree with
            // the schema about that.
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.DumpFileId).HasColumnName("dump_file_id");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.MimeType).HasColumnName("mime_type");
            e.Property(x => x.ByteSize).HasColumnName("byte_size");
            e.Property(x => x.StorageKey).HasColumnName("storage_key");
            e.Property(x => x.Checksum).HasColumnName("checksum");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");

            // ocr_status and ocr_text are left unmapped on purpose. V0 does not
            // read images, and an unmapped column cannot be set by accident — a
            // row claiming OCR was attempted would be SPEC.md 46's failure with a
            // database column behind it (ADR 0014).
        });

        b.Entity<AttachmentLink>(e =>
        {
            e.ToTable("attachment_links");
            e.HasKey(x => new { x.AttachmentId, x.EntityType, x.EntityId });
            e.Property(x => x.AttachmentId).HasColumnName("attachment_id");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");

            // Declared so EF orders the INSERTs, which is the third time this has
            // bitten (couple_members before couples, conversation_messages,
            // memories superseding themselves): EF orders writes by the
            // relationships in the model, so a foreign key the model does not know
            // about is a foreign key EF will violate. No navigation property is
            // needed, or wanted.
            e.HasOne<Attachment>().WithMany().HasForeignKey(x => x.AttachmentId);
        });

        b.Entity<CalendarEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.StartsAt).HasColumnName("starts_at");
            e.Property(x => x.EndsAt).HasColumnName("ends_at");
            e.Property(x => x.AllDay).HasColumnName("all_day");
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.RecurrenceRule).HasColumnName("recurrence_rule");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        b.Entity<Expense>(e =>
        {
            e.ToTable("expenses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");

            // Stated rather than inferred. EF would map decimal to numeric with the
            // provider's default precision, and SPEC.md 56.7 wants the arithmetic to
            // be the database's numeric(14,2) and nothing else.
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(14,2)");

            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Merchant).HasColumnName("merchant");
            e.Property(x => x.PaidBy).HasColumnName("paid_by");
            e.Property(x => x.IsShared).HasColumnName("is_shared");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            // A date, not a timestamp. Writing a timestamptz here would be silently
            // truncated by PostgreSQL and the truncation would use UTC.
            e.Property(x => x.OccurredOn).HasColumnName("occurred_on").HasColumnType("date");
        });

        b.Entity<ExpenseCategory>(e =>
        {
            e.ToTable("expense_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.Name).HasColumnName("name");
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
            e.Property(x => x.Result).HasColumnName("result").HasColumnType("jsonb");
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
