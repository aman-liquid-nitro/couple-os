using CoupleOS.Application.Attachments;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Conversation;
using CoupleOS.Application.Identity;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Reading;
using CoupleOS.Application.Time;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Application.Security;
using CoupleOS.Infrastructure.Identity;
using CoupleOS.Infrastructure.Persistence;
using CoupleOS.Infrastructure.Storage;
using CoupleOS.Infrastructure.Security;
using CoupleOS.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CoupleOS.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence and the couple-scoping seam.
    /// </summary>
    /// <param name="connectionString">
    /// Must name the non-superuser application role. A superuser connection
    /// bypasses every row-level security policy and silently voids ADR 0005;
    /// the integration tests demonstrate exactly that failure.
    /// </param>
    public static IServiceCollection AddCoupleOsInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<CoupleOsDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql
                .MapEnum<Visibility>("visibility")
                .MapEnum<ActionOutcome>("action_outcome")
                .MapEnum<DumpFileKind>("dump_file_kind")
                .MapEnum<DumpBlockStatus>("dump_block_status")
                .MapEnum<MessageRole>("message_role")

                // M3. task_status is deliberately absent: no V0 tool writes it and
                // nothing reads it yet, so the entity leaves the column to its
                // default and there is no CLR enum to keep in step. It arrives with
                // the read surface that filters on it.
                .MapEnum<TaskItemKind>("task_kind")
                .MapEnum<PriorityLevel>("priority_level")

                // M3's memory tools. data_source is shared by five tables and
                // mapped here for the one that writes it deliberately (see the
                // enum's own note); memory_status is mapped because superseding
                // writes it, not because anything filters on it yet.
                .MapEnum<MemoryType>("memory_type")
                .MapEnum<MemoryAssertion>("memory_assertion")
                .MapEnum<MemoryStatus>("memory_status")
                .MapEnum<DataSource>("data_source")));

        // Identity runs on the same database and the same non-superuser role, but
        // outside the couple scope — see IdentityDbContext for why that has to be
        // a separate context rather than more DbSets on the one above. No enum
        // mappings: auth_tokens.purpose is text, converted in the model.
        services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(connectionString));

        // One instance per request, two interfaces onto it.
        services.AddScoped<CoupleScopeHolder>();
        services.AddScoped<ICoupleScopeAccessor>(sp => sp.GetRequiredService<CoupleScopeHolder>());
        services.AddScoped<ICoupleScopeSetter>(sp => sp.GetRequiredService<CoupleScopeHolder>());

        services.AddScoped<IScopedUnitOfWork, CoupleScopedUnitOfWork>();

        services.AddScoped<IShoppingItemWriter, ShoppingItemWriter>();
        services.AddScoped<ITaskWriter, TaskWriter>();
        services.AddScoped<IEventWriter, EventWriter>();
        services.AddScoped<IExpenseWriter, ExpenseWriter>();
        services.AddScoped<IExpenseCategoryLookup, ExpenseCategoryLookup>();
        services.AddScoped<IMemoryWriter, MemoryWriter>();

        // On the couple-scoped context, like every other read that runs inside a
        // tool call. It is raw SQL, which makes that placement the whole of its
        // security: no set_config, no rows.
        services.AddScoped<IMemorySearch, MemorySearch>();

        // On IdentityDbContext, because couple_members has no row-level security.
        // Same seam as the clock below, and the same reason.
        services.AddScoped<IPartnerLookup, PartnerLookup>();

        services.AddScoped<IDumpFileStore, DumpFileStore>();
        services.AddScoped<IDumpBlockStore, DumpBlockStore>();
        services.AddScoped<IDumpRunStore, DumpRunStore>();

        services.AddScoped<IConversationStore, ConversationStore>();

        // The rows are scoped, and the bytes are not: a disk has no couple.
        // Splitting them is ADR 0014's point, and the registration lifetimes say
        // it out loud — one per request, one for the process.
        services.AddScoped<IAttachments, AttachmentStore>();
        services.AddScoped<ICapturedRecords, CapturedRecords>();
        services.TryAddSingleton(new AttachmentStorageOptions());
        services.AddSingleton<IAttachmentStore, FilesystemAttachmentStore>();

        // Scoped, because it caches the couple's zone for the request: a dump run
        // resolves a date per block and a couple does not move between them.
        services.AddScoped<ICoupleClock, CoupleClock>();

        services.AddScoped<IToolAuditSink, AiActionAuditSink>();

        services.AddScoped<IAuthTokenStore, AuthTokenStore>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped<IUserDirectory, UserDirectory>();

        // Injected rather than called statically so expiry and rate-limit windows
        // are testable without waiting fifteen real minutes.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Mail delivery, separate from persistence because the two fail for unrelated
    /// reasons and a deployment may well want one without the other.
    /// </summary>
    /// <param name="isDevelopment">
    /// When true, delivery failures are logged and swallowed and every message is
    /// written to the log — ADR 0007 requires the project to run locally with no
    /// mail provider at all. In production a failed send must throw, because there
    /// is no log for the user to read the link from.
    /// </param>
    public static IServiceCollection AddCoupleOsMail(
        this IServiceCollection services,
        SmtpOptions options,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        if (isDevelopment)
        {
            services.AddSingleton<IEmailSender>(sp => new DevelopmentEmailSender(
                new SmtpEmailSender(options),
                sp.GetRequiredService<ILogger<DevelopmentEmailSender>>()));
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        return services;
    }
}
