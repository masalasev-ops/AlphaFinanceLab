using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AlphaLab.Data;

/// <summary>
/// Design-time factory so `dotnet ef` can build the model without a running host. When invoked
/// bare (no --connection), it defaults to arena "sp500" and the portable compiled fallback
/// connection string (%LOCALAPPDATA%\AlphaLab\sp500\alphalab.db), resolving as a WRITER
/// (ensureDirectory:true — creating the store is a writer's job, D59).
///
/// Real migrations do NOT rely on this fallback: tools/migrate.ps1 reads
/// ConnectionStrings:AlphaLab from the Worker's appsettings.json and passes it via
/// `dotnet dotnet-ef database update --connection ...`, which overrides the value below
/// (finding 119). EF passes the explicit --connection to this factory as args[…]; we honor it.
/// </summary>
public sealed class AlphaLabDbContextFactory : IDesignTimeDbContextFactory<AlphaLabDbContext>
{
    public AlphaLabDbContext CreateDbContext(string[] args)
    {
        // Honor an explicit --connection passed by `dotnet ef ... --connection "<cs>"`.
        var explicitConnection = ExtractConnectionArg(args);

        var resolved = explicitConnection is not null
            ? DbPathResolver.Resolve(explicitConnection, DbPathResolver.DefaultArenaId)
            : DbPathResolver.Resolve(DbPathResolver.DefaultConnectionString, DbPathResolver.DefaultArenaId);

        var options = new DbContextOptionsBuilder<AlphaLabDbContext>()
            .UseSqlite(resolved)
            .Options;

        return new AlphaLabDbContext(options);
    }

    private static string? ExtractConnectionArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--connection", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
