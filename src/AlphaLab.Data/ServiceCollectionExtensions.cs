using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AlphaLabDbContext"/> against the arena-namespaced SQLite file.
    /// </summary>
    /// <param name="ensureDirectory">
    /// true (default) — writers (the Worker, the EF design-time factory) resolve the path AND
    /// create the store's directory (D59: creating the store is a writer's job).
    /// false — readers (the API) resolve the path only and NEVER touch the filesystem at boot,
    /// so the API never creates the real DB directory (v1.9.6).
    /// </param>
    public static IServiceCollection AddAlphaLabData(
        this IServiceCollection services,
        string connectionString,
        string arenaId,
        bool ensureDirectory = true)
    {
        var resolved = ensureDirectory
            ? DbPathResolver.Resolve(connectionString, arenaId)
            : DbPathResolver.ResolvePath(connectionString, arenaId);

        services.AddDbContext<AlphaLabDbContext>(options => options.UseSqlite(resolved));
        return services;
    }
}
