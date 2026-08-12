using CoupleOS.Application.Capture;
using CoupleOS.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleOS.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCoupleOsApplication(this IServiceCollection services)
    {
        services.AddCoupleOsTools();
        services.AddScoped<ICaptureProcessor, CaptureProcessor>();

        return services;
    }
}
