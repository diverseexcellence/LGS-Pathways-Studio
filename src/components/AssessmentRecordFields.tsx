import React from 'react';
import { Trash2 } from 'lucide-react';
import {
  AssessmentInput, AssessmentSource, ASSESSMENT_SOURCES, SOURCE_PERIODS,
  SOURCE_PROFICIENCY_FALLBACK, TierRuleset,
} from '../lib/api';

export const EMPTY_RECORD: AssessmentInput = {
  uploadType: 'ILEARN',
  subject: 'ELA',
  period: '',
  score: null,
  proficiency: '',
  date: '',
};

function isSource(value: string): value is AssessmentSource {
  return (ASSESSMENT_SOURCES as readonly string[]).includes(value);
}

function titleCase(label: string) {
  return label.replace(/\b[a-z]/g, c => c.toUpperCase());
}

// "on grade level" and "on grade" are the same band. Staff-facing labels drop the trailing
// "level" so the dropdown matches the wording already shown on the assessment record.
function withoutLevel(label: string) {
  return label.trim().replace(/\s+/g, ' ').replace(/\s+level$/i, '');
}

/**
 * Performance levels for a source, taken from the live ruleset so the list can only ever offer
 * labels the engine maps to a 0-3 value. A label outside the ruleset resolves to nothing: the
 * record would save, display on the profile, and then be dropped from the score as
 * `unrecognized_category` — which is precisely what a free-text field would invite.
 *
 * The ruleset lists several spellings per value ("far below grade" / "far below grade level").
 * The option is the spelling without "level", ordered weakest to strongest.
 */
export function proficiencyOptions(source: string, ruleset: TierRuleset | null): string[] {
  const key = Object.keys(ruleset?.categoryValues ?? {})
    .find(k => k.toLowerCase() === source.toLowerCase());
  const values = key ? ruleset!.categoryValues[key] : undefined;

  if (values && Object.keys(values).length > 0) {
    const preferred = new Map<number, { text: string; fromLevel: boolean }>();
    for (const [label, value] of Object.entries(values)) {
      const fromLevel = /\blevel\b/i.test(label);
      const held = preferred.get(value);
      // Keep the spelling that was written without "level". Either way the option text drops it.
      if (!held || (held.fromLevel && !fromLevel))
        preferred.set(value, { text: withoutLevel(label), fromLevel });
    }
    return [...preferred.entries()]
      .sort((a, b) => a[0] - b[0])
      .map(([, option]) => titleCase(option.text));
  }

  return isSource(source) ? SOURCE_PROFICIENCY_FALLBACK[source] : [];
}

/** The dropdown label for a stored proficiency, so "On grade" and "On Grade Level" both select "On Grade". */
export function matchProficiencyOption(
  source: string,
  raw: string | null | undefined,
  ruleset: TierRuleset | null,
): string | null {
  const text = (raw ?? '').trim();
  if (!text) return null;
  const needle = withoutLevel(text).toLowerCase();
  return proficiencyOptions(source, ruleset).find(option => withoutLevel(option).toLowerCase() === needle) ?? null;
}

const inputClass =
  'w-full px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none';

// One line, same height, so a longer caption cannot drop its control below the others.
const labelClass = 'block text-xs font-medium text-slate-500 mb-1 h-4 leading-4 whitespace-nowrap';

/**
 * One assessment record's fields. Shared by the add-student form and the profile's record modal so
 * both paths collect and normalize the same inputs.
 */
export default function AssessmentRecordFields({
  value, onChange, ruleset, onRemove, index,
}: {
  value: AssessmentInput;
  onChange: (next: AssessmentInput) => void;
  ruleset: TierRuleset | null;
  onRemove?: () => void;
  index?: number;
}) {
  const source = value.uploadType;
  const periods = isSource(source) ? SOURCE_PERIODS[source] : [];
  const levels = proficiencyOptions(source, ruleset);
  const selectedLevel = matchProficiencyOption(source, value.proficiency, ruleset) ?? value.proficiency ?? '';
  const set = (patch: Partial<AssessmentInput>) => onChange({ ...value, ...patch });

  // IREAD is excluded from the weighted calculation by the ruleset (AC-09), so say so at entry
  // time rather than letting staff enter records and wonder why the tier never moves.
  const excluded = (ruleset?.excludedSources ?? ['IREAD'])
    .some(s => s.toLowerCase() === source.toLowerCase());

  return (
    <div className="border border-slate-200 rounded-lg p-4 bg-slate-50/60">
      <div className="flex items-center justify-between mb-3">
        <p className="text-xs font-semibold text-slate-500 uppercase tracking-wide">
          {index === undefined ? 'Assessment Record' : `Record ${index + 1}`}
        </p>
        {onRemove && (
          <button
            type="button"
            onClick={onRemove}
            className="text-slate-400 hover:text-red-500 transition-colors"
            title="Remove this record"
          >
            <Trash2 className="w-4 h-4" />
          </button>
        )}
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
        <div>
          <label className={labelClass}>Source</label>
          <select
            value={source}
            // Period and performance-level vocabularies are per-source, so carrying the old
            // values across a source change would store labels this source can't be scored on.
            onChange={e => set({ uploadType: e.target.value, period: '', proficiency: '' })}
            className={inputClass}
          >
            {ASSESSMENT_SOURCES.map(s => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>

        <div>
          <label className={labelClass}>Subject</label>
          <select
            value={value.subject ?? ''}
            onChange={e => set({ subject: e.target.value })}
            className={inputClass}
          >
            <option value="ELA">ELA</option>
            <option value="Math">Math</option>
            <option value="Reading">Reading</option>
          </select>
        </div>

        <div>
          <label className={labelClass} title="The period sets this record's evidence weight">
            Period
          </label>
          <select
            value={value.period ?? ''}
            onChange={e => set({ period: e.target.value })}
            className={inputClass}
          >
            <option value="">Not specified</option>
            {periods.map(p => <option key={p} value={p}>{p}</option>)}
          </select>
        </div>

        <div>
          <label className={labelClass}>Performance Level</label>
          <select
            value={selectedLevel}
            onChange={e => set({ proficiency: e.target.value })}
            className={inputClass}
          >
            <option value="">Not specified</option>
            {levels.map(l => <option key={l} value={l}>{l}</option>)}
          </select>
        </div>

        <div>
          <label className={labelClass}>Score</label>
          <input
            type="number"
            step="any"
            value={value.score ?? ''}
            onChange={e => set({ score: e.target.value === '' ? null : Number(e.target.value) })}
            placeholder="Optional"
            className={inputClass}
          />
        </div>

        <div>
          <label className={labelClass}>Date Taken</label>
          <input
            type="date"
            value={value.date ?? ''}
            onChange={e => set({ date: e.target.value })}
            className={inputClass}
          />
        </div>
      </div>

      {!value.period && (
        <p className="text-xs text-amber-700 mt-2">
          Without a period this record carries no evidence weight, so it will not count toward the tier score.
        </p>
      )}
      {excluded && (
        <p className="text-xs text-slate-500 mt-2">
          {source} is excluded from the weighted tier calculation by the current ruleset — this record
          will show on the profile but will not change the tier.
        </p>
      )}
    </div>
  );
}
