using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AlphaLab.Core.Config;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>What one refresh fetched: the parsed observations and the fingerprint of the bytes they
/// came from.</summary>
/// <param name="Observations">Every observation across both files, already decimal and ISO-dated.</param>
/// <param name="Fingerprint">SHA-256 over the RAW ZIP BYTES of both files, in a fixed order.</param>
/// <param name="Files">The source URLs, for `factor_refresh_log.files_json`.</param>
public sealed record FactorFetch(
    IReadOnlyList<FactorObservation> Observations,
    string Fingerprint,
    IReadOnlyList<string> Files);

/// <summary>The fetch half of the D41 refresh. Separated from the write half so the provider performs
/// ZERO DB writes — the rule-12 shape `MembershipRefreshStep` already established.</summary>
public interface IFactorDataProvider
{
    Task<FactorFetch> FetchAsync(CancellationToken ct = default);
}

/// <summary>
/// Fetches the two Ken French daily zips (INTEGRATIONS §3), unzips each, decodes latin1, and parses.
/// No DB access at all.
///
/// **LATIN1, NOT UTF-8, AND IT IS NOT COSMETIC.** INTEGRATIONS §3 records the rule. The files carry
/// high-range bytes in their prose preamble; decoding them as UTF-8 yields U+FFFD, and — worse — a
/// UTF-8 decode of a ZIP is lossy long before that, which is why the bytes arrive through
/// <see cref="IResilientBinaryFetcher"/> rather than the text port. `Encoding.Latin1` is in-box, so this
/// needs no package.
///
/// **THE FINGERPRINT IS OVER THE RAW ZIP BYTES.** The only in-repo hashing precedent
/// (`HistoricalBackfill`) hashes a UTF-8 string, which cannot be reused here for exactly the reason
/// above. What the fingerprint is COMPARED against is not this class's job — see
/// <see cref="FactorRefresh"/>, where the comparison subject is stated and the refusal lives.
/// </summary>
public sealed class FrenchFactorProvider(IResilientBinaryFetcher http, FactorDataOptions options)
    : IFactorDataProvider
{
    public async Task<FactorFetch> FetchAsync(CancellationToken ct = default)
    {
        // Fixed order: the fingerprint must not depend on completion order, or an unchanged upstream
        // would produce a different hash on a re-run and the "upstream revised history" alarm would be
        // noise rather than signal.
        var sources = new[]
        {
            (Url: options.FiveFactorDailyUrl, Name: "five_factor_daily"),
            (Url: options.MomentumDailyUrl, Name: "momentum_daily"),
        };

        var all = new List<FactorObservation>();
        var hash = SHA256.Create();
        var files = new List<string>();

        foreach (var (url, name) in sources)
        {
            var zipBytes = await http.GetBytesAsync(url, $"french_{name}", ct).ConfigureAwait(false);
            hash.TransformBlock(zipBytes, 0, zipBytes.Length, null, 0);

            var csv = ReadSingleEntryAsLatin1(zipBytes, url);
            all.AddRange(FrenchFactorCsvParser.Parse(csv));
            files.Add(url);
        }

        hash.TransformFinalBlock([], 0, 0);
        var fingerprint = Convert.ToHexStringLower(hash.Hash!);

        return new FactorFetch(all, fingerprint, files);
    }

    /// <summary>Opens the zip and reads its FIRST entry, per INTEGRATIONS §3's "read the single inner
    /// CSV (`namelist()[0]`)". A zip that holds no entry, or that is not a zip at all (an HTML error
    /// page served with 200), refuses here rather than reaching the parser as confusing text.</summary>
    private static string ReadSingleEntryAsLatin1(byte[] zipBytes, string url)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(ms, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new FrenchFactorFormatException(
                $"The payload from {Redact(url)} is not a readable zip ({ex.Message}). A 200 response " +
                "carrying an HTML error page looks exactly like this — the INTEGRATIONS §3 note about " +
                "the required `ftp/` URL segment is the usual cause.");
        }

        using (archive)
        {
            if (archive.Entries.Count == 0)
            {
                throw new FrenchFactorFormatException($"The zip from {Redact(url)} contains no entries.");
            }

            using var entry = archive.Entries[0].Open();
            using var reader = new StreamReader(entry, Encoding.Latin1);
            return reader.ReadToEnd();
        }
    }

    /// <summary>These URLs carry no credential, but redaction is the house rule for anything a fetch
    /// error puts in a log (D67, hard rule 11) and a future keyed source must not depend on someone
    /// remembering to add it.</summary>
    private static string Redact(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : string.Concat(url.AsSpan(0, q), "?<redacted>");
    }
}
