// Every date in the app renders in US format, never the viewer's browser locale.
//
// LGS is an Indiana school corporation and every source export is month-first (verified across
// ILEARN, IXL and Acadience files: 1,226 date values, none day-first). Leaving formatting to
// toLocaleDateString() meant the same record read as "5/11/2025" for a viewer on a day-first
// locale and "11/5/2025" for one in the US — the same stored date shown as two different days,
// with nothing on screen to say which was meant.

export const US_LOCALE = 'en-US';

/**
 * Parses the mixed date formats found across LGS source files into a timestamp:
 * ISO ("2017-03-21", "2026-01-01T00:00:00Z"), US ("11/5/2025"), parenthesised IXL
 * ("(10/29/2025)") and 2-digit-year Acadience ("11/18/25").
 *
 * Returns NaN when nothing usable is present (IXL writes "--" for a missing date).
 */
export function parseFlexibleDate(raw?: string | null): number {
  const cleaned = (raw || '').trim().replace(/^\(|\)$/g, '');
  if (!cleaned || cleaned === '--') return NaN;

  // ISO first, and taken apart by hand rather than passed to new Date(): the yyyy-mm-dd form is
  // parsed as UTC midnight, which renders as the previous day for viewers behind UTC.
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})/.exec(cleaned);
  if (iso) return new Date(+iso[1], +iso[2] - 1, +iso[3]).getTime();

  const parts = cleaned.split(/[/\-]/).map(p => p.trim());
  if (parts.length === 3 && parts.every(p => /^\d+$/.test(p))) {
    const [a, b, y] = parts.map(Number);
    const year = y < 100 ? 2000 + y : y;
    // Month-first unless the first segment can only be a day. No source uses day-first.
    const [month, day] = a > 12 ? [b, a] : [a, b];
    const ts = new Date(year, month - 1, day).getTime();
    if (!isNaN(ts)) return ts;
  }

  const native = new Date(cleaned).getTime();
  return isNaN(native) ? NaN : native;
}

/** A calendar date as US M/D/YYYY. Falls back to the raw text when it cannot be parsed. */
export function formatUsDate(raw?: string | null, fallback = 'N/A'): string {
  if (!raw) return fallback;
  const ts = parseFlexibleDate(raw);
  if (isNaN(ts)) return raw || fallback;
  return new Date(ts).toLocaleDateString(US_LOCALE, {
    year: 'numeric', month: 'numeric', day: 'numeric',
  });
}

/** A calendar date as US "Nov 5, 2025" — for places where the month name reads better. */
export function formatUsDateMedium(raw?: string | null, fallback = 'N/A'): string {
  if (!raw) return fallback;
  const ts = parseFlexibleDate(raw);
  if (isNaN(ts)) return raw || fallback;
  return new Date(ts).toLocaleDateString(US_LOCALE, {
    year: 'numeric', month: 'short', day: 'numeric',
  });
}

/** An instant (audit entries, upload logs, AI summaries) as US date + time. */
export function formatUsDateTime(iso?: string | null, fallback = 'N/A'): string {
  if (!iso) return fallback;
  const ts = new Date(iso).getTime();
  if (isNaN(ts)) return fallback;
  return new Date(ts).toLocaleString(US_LOCALE, {
    year: 'numeric', month: 'short', day: 'numeric',
    hour: 'numeric', minute: '2-digit',
  });
}

/** Time only, US 12-hour clock. */
export function formatUsTime(iso?: string | null, fallback = ''): string {
  if (!iso) return fallback;
  const ts = new Date(iso).getTime();
  if (isNaN(ts)) return fallback;
  return new Date(ts).toLocaleTimeString(US_LOCALE, { hour: 'numeric', minute: '2-digit' });
}
