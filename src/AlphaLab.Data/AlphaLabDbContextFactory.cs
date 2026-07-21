using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AlphaLab.Data;

/// <summary>
/// Design-time factory so `dotnet ef` can build the model without a running host. When invoked
/// bare (no --connection), it defaults to arena "sp500" and <see cref="DbPathResolver.DefaultConnectionString"/>
/// — the E: literal `Data Source=E:/AlphaLabDatabase/{Arena.Id}/alphalab.db`, which `ResolvePath`
/// normalizes to the running platform's separator (on Windows: `E:\AlphaLabDatabase\sp500\alphalab.db`)
/// — as a WRITER (ensureDirectory:true — creating the store is a writer's job, D59). This constant is
/// INTENTIONALLY the E: literal, byte-identical to both appsettings (the three-spots rule,
/// DB_RELOCATION.md, guarded by ConfigConsistencyTests). Do NOT "correct" it to a {LocalAppData} form
/// to match a portability assumption, and do NOT "correct" the forward slashes back to backslashes
/// (they are deliberate, v1.9.36 — the separator normalization is what keeps one string valid on
/// Windows and Linux alike) — either edit would redden ConfigConsistencyTests or, worse, make the
/// three spots disagree with the deployed store. To move the store off E:, change all three spots
/// together per DB_RELOCATION.md.
///
/// Real migrations do NOT rely on this fallback: tools/migrate.ps1 reads
/// ConnectionStrings:AlphaLab from the Worker's appsettings.json, resolves the `{Arena.Id}` /
/// `{LocalAppData}` tokens itself, and passes the fully-resolved string via
/// `dotnet dotnet-ef database update --connection ...`, which overrides the constant below
/// (finding 119). EF passes that explicit --connection to this factory as args[…]; we honor it and
/// expect it to be already token-resolved (a still-tokenized value would resolve under the default
/// arena "sp500", not any -Arena the caller intended).
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
