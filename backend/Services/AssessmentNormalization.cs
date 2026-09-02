using System.Globalization;

namespace LgsImpact.Api.Services;

/// <summary>
/// Subject / period / date normalization shared by ingest (UploadController) and the tier engine
/// (TierCalculationService). Extracted from UploadController so both consumers — and the backfill
/// / data-quality endpoints — use exactly one implementation instead of drifting copies.
/// </summary>
public static class AssessmentNormalization
{
    // ─── Subject ────────────────────────────────────────────────────────────────

    /// <summary>Best-effort subject detection when no Subject/Content Area column is present.
    /// Acadience and I-Read are always "Reading" — checked before the generic ELA/Reading check.</summary>
    public static string DetectSubject(string uploadType, string fileName, Dictionary<string, string>? row = null)
    {
        if (uploadType.Equals("Acadience", StringComparison.OrdinalIgnoreCase) ||
            uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Acadience", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("IREAD", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("I-READ", StringComparison.OrdinalIgnoreCase)) return "Reading";

        if (uploadType.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("EnglishLanguageArts", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (uploadType.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Mathematics", StringComparison.OrdinalIgnoreCase)) return "Math";

        // Filename gave no signal. Combined IXL exports contain neither "ELA" nor "Math" in the
        // name, so fall back to the columns present in the row, which are unambiguous.
        if (row is not null)
        {
            var hasEla = row.Keys.Any(k => k.Contains("Overall ELA", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("Overall reading", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("Overall writing", StringComparison.OrdinalIgnoreCase));
            var hasMath = row.Keys.Any(k => k.Contains("Overall math", StringComparison.OrdinalIgnoreCase));
            if (hasEla && !hasMath) return "ELA";
            if (hasMath && !hasEla) return "Math";
        }

        return "Mixed"; // genuinely ambiguous — the tier engine will treat this as unknown_subject
    }

    /// <summary>Normalizes a raw Subject value to "ELA" | "Math" | "Reading" | pass-through.
    /// This is the ingest-time normalizer; the tier engine also applies
    /// <c>TierRulesetConfigDocument.SourceSubjectOverrides</c> on top of the stored value
    /// (e.g. routing Acadience's "Reading" into ELA) rather than rewriting it here.</summary>
    public static string NormalizeSubject(string? subject)
    {
        if (subject is null) return "Mixed";
        if (subject.Equals("Reading", StringComparison.OrdinalIgnoreCase)) return "Reading";
        if (subject.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (subject.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        return subject;
    }

    // ─── I-Read proficiency ─────────────────────────────────────────────────────

    /// <summary>Maps I-Read pass/fail status strings to standard proficiency labels. IREAD is
    /// excluded from the weighted tier calculation (AC-09) but this keeps its display consistent.</summary>
    public static string? NormalizeIReadProficiency(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Contains("Did Not Pass", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("F", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Not Passed", StringComparison.OrdinalIgnoreCase)) return "Below Proficiency";
        if (v.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("P", StringComparison.OrdinalIgnoreCase)) return "At Proficiency";
        if (v.Contains("Waived", StringComparison.OrdinalIgnoreCase)) return "Waived";
        if (v.Contains("Exempt", StringComparison.OrdinalIgnoreCase)) return "Exempt";
        return raw;
    }

    // ─── Period normalization ───────────────────────────────────────────────────

    /// <summary>Extracts BOY/MOY/EOY from an Acadience Period column value or filename.</summary>
    public static string? NormalizeAcadiencePeriod(string? periodCol, string fileName)
    {
        var candidates = new[] { periodCol ?? "", fileName };
        foreach (var s in candidates)
        {
            if (s.Contains("BOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Beginning", StringComparison.OrdinalIgnoreCase)) return "BOY";
            if (s.Contains("MOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Middle", StringComparison.OrdinalIgnoreCase)) return "MOY";
            if (s.Contains("EOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("End", StringComparison.OrdinalIgnoreCase)) return "EOY";
        }
        return periodCol;
    }

    /// <summary>Extracts CP1/CP2/CP3/SPRING from an ILEARN Period column value or filename.
    /// Checkpoints are checked before Spring so a file like "ILEARN-Checkpoint2-Spring2026"
    /// still resolves to CP2.</summary>
    public static string? NormalizeIlearnPeriod(string? periodCol, string fileName)
    {
        var candidates = new[] { periodCol ?? "", fileName };
        foreach (var s in candidates)
        {
            if (s.Contains("CP1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Cycle 1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("CP-1", StringComparison.OrdinalIgnoreCase)) return "CP1";
            if (s.Contains("CP2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Cycle 2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("CP-2", StringComparison.OrdinalIgnoreCase)) return "CP2";
            if (s.Contains("CP3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Cycle 3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("CP-3", StringComparison.OrdinalIgnoreCase)) return "CP3";
        }
        foreach (var s in candidates)
        {
            if (s.Contains("Spring", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Summative", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("SUMM", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("End of Year ILEARN", StringComparison.OrdinalIgnoreCase)) return "SPRING";
        }
        return null; // unresolved — excluded by the engine and surfaced in the data-quality report
    }

    /// <summary>Extracts BOY/MOY/EOY from an IXL Period/Benchmark column value or filename
    /// (e.g. LGS's "LevelUp-Diagnostic-Results-EOY-Math-....csv" convention). No date-window
    /// fallback here — see TierRulesetConfigDocument.IxlPeriodFromDateFallback for the opt-in
    /// month-based guess, applied by the tier engine, not at ingest.</summary>
    public static string? NormalizeIxlPeriod(string? periodCol, string fileName)
    {
        var candidates = new[] { periodCol ?? "", fileName };
        foreach (var s in candidates)
        {
            if (s.Contains("BOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Beginning", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic 1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic1", StringComparison.OrdinalIgnoreCase)) return "BOY";
            if (s.Contains("MOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Middle", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Mid", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic 2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic2", StringComparison.OrdinalIgnoreCase)) return "MOY";
            if (s.Contains("EOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("End", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic 3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Diagnostic3", StringComparison.OrdinalIgnoreCase)) return "EOY";
        }
        return null;
    }

    /// <summary>Dispatches to the correct per-source period normalizer.</summary>
    public static string? NormalizePeriod(string uploadType, string? periodCol, string fileName) => uploadType switch
    {
        _ when uploadType.Equals("Acadience", StringComparison.OrdinalIgnoreCase) => NormalizeAcadiencePeriod(periodCol, fileName),
        _ when uploadType.Equals("ILEARN", StringComparison.OrdinalIgnoreCase) => NormalizeIlearnPeriod(periodCol, fileName),
        _ when uploadType.Equals("IXL", StringComparison.OrdinalIgnoreCase) => NormalizeIxlPeriod(periodCol, fileName),
        _ => periodCol,
    };

    /// <summary>Resolves the period *key* the tier engine uses to look up an evidence weight —
    /// prefers the already-normalized <c>Period</c>, then re-derives from <c>PeriodRaw</c>/filename
    /// so historical rows ingested before normalization existed can still be scored.</summary>
    public static string? ResolvePeriodKey(string source, string? period, string? periodRaw, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(period)) return period.Trim().ToUpperInvariant();
        var reDerived = NormalizePeriod(source, periodRaw, fileName ?? "");
        return reDerived?.Trim().ToUpperInvariant();
    }

    // ─── Date parsing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a source-formatted date string into ISO "yyyy-MM-dd". Handles the mixed formats seen
    /// across LGS exports: ISO ("2025-08-19"), parenthesised IXL ("(11/13/2025)"), US
    /// ("02/25/2026") and 2-digit-year Acadience ("11/18/25").
    /// When the day/month order is genuinely ambiguous (both leading segments &lt;= 12),
    /// <paramref name="ambiguous"/> is set and month-first is assumed.
    /// <para><paramref name="source"/> no longer influences day/month order — every LGS source is
    /// month-first (see the note in the ambiguous branch). It is retained because callers pass it,
    /// and because a genuinely day-first feed would need it back.</para>
    /// </summary>
    public static string? TryParseFlexibleDate(string? raw, string? source, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim().Trim('(', ')').Trim();
        if (s.Length == 0) return null;

        // ISO already
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso.ToString("yyyy-MM-dd");

        var sep = s.Contains('/') ? '/' : s.Contains('-') ? '-' : (char?)null;
        if (sep is char sepChar)
        {
            var parts = s.Split(sepChar);
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var p0) &&
                int.TryParse(parts[1], out var p1) &&
                int.TryParse(parts[2], out var p2))
            {
                int year = p2 < 100 ? 2000 + p2 : p2;

                int month, day;
                if (p0 > 12) { day = p0; month = p1; }        // unambiguous d/m/y
                else if (p1 > 12) { month = p0; day = p1; }   // unambiguous m/d/y
                else
                {
                    // Month-first for every source. Acadience used to be treated as day-first, but
                    // the actual exports contradict that: across 1,226 date values in the live
                    // ILEARN, IXL and Acadience files, 654 prove month-first (a second segment
                    // above 12) and not one proves day-first. The old special case silently moved
                    // 95 of 422 rows in alo_reading_pm_data_2025-2026.csv by whole months
                    // (11/4/25 was stored as 11 April instead of 4 November).
                    ambiguous = true;
                    month = p0;
                    day = p1;
                }

                try { return new DateTime(year, month, day).ToString("yyyy-MM-dd"); }
                catch (ArgumentOutOfRangeException) { /* fall through to generic parse */ }
            }
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var generic))
            return generic.ToString("yyyy-MM-dd");

        return null;
    }
}
