using CoupleOS.Application.Attachments;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Conversation;
using CoupleOS.Application.Identity;
using CoupleOS.Application.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleOS.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCoupleOsApplication(this IServiceCollection services)
    {
        services.AddCoupleOsTools();
        services.AddScoped<ICaptureProcessor, CaptureProcessor>();
        services.AddScoped<IBlockProcessor, BlockProcessor>();
        services.AddScoped<ICaptureIntake, CaptureIntake>();
        services.AddScoped<ISharedFileEditor, SharedFileEditor>();
        services.AddScoped<IAttachmentIntake, AttachmentIntake>();

        // ADR 0009's other surface. It shares the tool registry and the dispatcher
        // registered above and nothing else, which is the whole of what "one
        // pipeline" means here.
        services.AddScoped<IPrivateThread, PrivateThread>();

        services.AddScoped<IMagicLinkService, MagicLinkService>();

        // TryAdd, so a host that binds these from configuration wins and a test
        // that does not still gets ADR 0007's values rather than a null reference.
        services.TryAddSingleton(new IdentityOptions());
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
