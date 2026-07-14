using System.Text;

namespace AlphaLab.Data.Providers;

/// <summary>
/// A minimal RFC-4180 CSV reader (no package — decision #2). Fields may be quoted; a quoted field
/// can contain commas, CR/LF newlines, and "" escaped quotes. Records split on LF or CRLF that fall
/// OUTSIDE a quoted field. This is what keeps the iShares holdings footer — a single quoted field
/// spanning ten physical lines — as ONE field, and the in-field thousands-comma numerics intact
/// (INTEGRATIONS §2). Tolerant of a trailing newline (no phantom final record) and a leading BOM.
/// A blank physical line yields a one-element row [""] so callers can detect the data/footer boundary.
/// </summary>
public static class DelimitedCsvReader
{
    private const char Bom = (char)0xFEFF;

    public static IReadOnlyList<string[]> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var recordHasContent = false; // any char or separator seen since the last record boundary

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRecord()
        {
            EndField();
            rows.Add(fields.ToArray());
            fields.Clear();
            recordHasContent = false;
        }

        var start = content.Length > 0 && content[0] == Bom ? 1 : 0; // strip a leading BOM if present
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is a literal quote; a lone " closes the quoted section.
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { field.Append(c); }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case ',':
                    EndField();
                    recordHasContent = true;
                    break;
                case '\r':
                    EndRecord();
                    if (i + 1 < content.Length && content[i + 1] == '\n') i++; // consume the LF of CRLF
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    recordHasContent = true;
                    break;
            }
        }

        // Flush a final record only if the content did not end exactly on a newline.
        if (recordHasContent || field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }

        return rows;
    }
}
