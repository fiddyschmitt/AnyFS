using AnyFS.MountProviders.WebDAV.Stores;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server.Stores;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.WebDAV
{
    public static class Extensions
    {
        public static IServiceCollection AddStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(this IServiceCollection services) where TStore : class, IStore
            => services.AddScoped<IStore, TStore>();

        public static IServiceCollection AddAnyFsStore(this IServiceCollection services, Action<AnyFsStoreOptions>? configure = null)
        {
            return services
                .Configure<AnyFsStoreOptions>(opts =>
                {
                    configure?.Invoke(opts);
                })
                .AddAnyFsStore<AnyFsStore>();
        }

        public static IServiceCollection AddAnyFsStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAnyFsStore>(this IServiceCollection services)
            where TAnyFsStore : AnyFsStoreBase
        {
            return services
                .AddSingleton<AnyFsStoreCollectionPropertyManager>()
                .AddSingleton<AnyFsStoreItemPropertyManager>()
                .AddStore<TAnyFsStore>();
        }
    }
}
