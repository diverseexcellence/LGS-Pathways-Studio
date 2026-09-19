import React, { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { UserPlus, Plus, Sparkles, AlertTriangle } from 'lucide-react';
import { studentsApi, configApi, AssessmentInput, StudentInput, TierRuleset } from '../lib/api';
import AssessmentRecordFields, { EMPTY_RECORD } from '../components/AssessmentRecordFields';

const inputClass =
  'w-full px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none';

const YES_NO = [
  { value: '', label: 'Not recorded' },
  { value: 'Yes', label: 'Yes' },
  { value: 'No', label: 'No' },
];

const EMPTY_FORM: StudentInput = {
  fullName: '', dob: '', stn: '', localId: '', classGroup: '', grade: '', gender: '',
  ethnicity: '', ellStatus: '', spedStatus: '', section504: '', homeRoom: '',
  entryDate: '', exitDate: '', lunchStatus: '', zipCode: '',
};

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-xs font-medium text-slate-500 mb-1">
        {label}
        {hint && <span className="ml-1 font-normal text-slate-400">{hint}</span>}
      </label>
      {children}
    </div>
  );
}

/**
 * Enrol one student by hand, with as much of their assessment history as is available, and let the
 * tier engine score them on save — the same normalization and the same engine a CSV import uses.
 * Exists for the students who arrive between file drops: previously the only way to get a student
 * into the system was to wait for the next roster export.
 */
export default function StudentNew() {
  const navigate = useNavigate();
  const [form, setForm] = useState<StudentInput>(EMPTY_FORM);
  const [records, setRecords] = useState<AssessmentInput[]>([]);
  const [ruleset, setRuleset] = useState<TierRuleset | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  // Set when the backend reports a matching identifier. Saving again with allowDuplicate is a
  // deliberate second step, never the default — a duplicate record splits a student's evidence
  // across two profiles and neither one tiers correctly.
  const [duplicate, setDuplicate] = useState<string | null>(null);

  useEffect(() => {
    configApi.getTierRules().then(setRuleset).catch(() => {});
  }, []);

  const set = (patch: Partial<StudentInput>) => {
    setForm(prev => ({ ...prev, ...patch }));
    setDuplicate(null);
  };

  const minDataPoints = ruleset?.minDataPoints ?? 2;
  const weightedRecords = records.filter(r => r.period && r.proficiency).length;

  async function save(allowDuplicate: boolean) {
    if (!form.fullName?.trim()) {
      setError('Full name is required.');
      return;
    }
    setSaving(true);
    setError('');
    try {
      const created = await studentsApi.create({ ...form, records, allowDuplicate });
      navigate(`/students/${created.studentId}`);
    } catch (e: any) {
      // A matching identifier is a prompt, not a dead end — the message names the existing student
      // and offers the override, rather than making the user retype the form somewhere else.
      if (!allowDuplicate && /already exists/i.test(e.message ?? '')) setDuplicate(e.message);
      else setError(e.message || 'Could not create the student.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-6 max-w-4xl mx-auto">
      <button
        onClick={() => navigate('/students')}
        className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-lgs-blue transition-colors"
      >
        ← Back to Students List
      </button>

      <div>
        <h1 className="text-2xl font-bold text-lgs-blue flex items-center gap-2">
          <UserPlus className="w-6 h-6 text-lgs-red" />
          Add Student
        </h1>
        <p className="text-slate-500 mt-1 text-sm">
          Enrol one student and enter the assessment results you have. The tier engine runs on save,
          using the same rules as a file import.
        </p>
      </div>

      {/* ── Identity ─────────────────────────────────────────────────────── */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4">Student Identity</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          <Field label="Full Name *">
            <input
              value={form.fullName ?? ''}
              onChange={e => set({ fullName: e.target.value })}
              placeholder="First Last"
              className={inputClass}
            />
          </Field>
          <Field label="STN" hint="(state student number)">
            <input value={form.stn ?? ''} onChange={e => set({ stn: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Local ID" hint="(PowerSchool number)">
            <input value={form.localId ?? ''} onChange={e => set({ localId: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Date of Birth">
            <input type="date" value={form.dob ?? ''} onChange={e => set({ dob: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Gender">
            <input value={form.gender ?? ''} onChange={e => set({ gender: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Ethnicity">
            <input value={form.ethnicity ?? ''} onChange={e => set({ ethnicity: e.target.value })} className={inputClass} />
          </Field>
        </div>
        <p className="text-xs text-slate-400 mt-3">
          An STN or local ID lets later file imports attach this student's results to this record instead
          of creating a second one.
        </p>
      </div>

      {/* ── Enrollment ───────────────────────────────────────────────────── */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4">Enrollment</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          <Field label="Grade">
            <input value={form.grade ?? ''} onChange={e => set({ grade: e.target.value })} placeholder="e.g. 3 or K" className={inputClass} />
          </Field>
          <Field label="Class Group">
            <input value={form.classGroup ?? ''} onChange={e => set({ classGroup: e.target.value })} placeholder="Unassigned" className={inputClass} />
          </Field>
          <Field label="Homeroom">
            <input value={form.homeRoom ?? ''} onChange={e => set({ homeRoom: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Entry Date">
            <input type="date" value={form.entryDate ?? ''} onChange={e => set({ entryDate: e.target.value })} className={inputClass} />
          </Field>
          <Field label="Exit Date">
            <input type="date" value={form.exitDate ?? ''} onChange={e => set({ exitDate: e.target.value })} className={inputClass} />
          </Field>
          <Field label="ZIP Code">
            <input value={form.zipCode ?? ''} onChange={e => set({ zipCode: e.target.value })} className={inputClass} />
          </Field>
        </div>
      </div>

      {/* ── Programs ─────────────────────────────────────────────────────── */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4">Program &amp; Support Indicators</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <Field label="EL Status">
            <select value={form.ellStatus ?? ''} onChange={e => set({ ellStatus: e.target.value })} className={inputClass}>
              {YES_NO.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </Field>
          <Field label="Special Education">
            <select value={form.spedStatus ?? ''} onChange={e => set({ spedStatus: e.target.value })} className={inputClass}>
              {YES_NO.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </Field>
          <Field label="504 Plan">
            <select value={form.section504 ?? ''} onChange={e => set({ section504: e.target.value })} className={inputClass}>
              {YES_NO.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </Field>
          <Field label="Lunch Status">
            <input value={form.lunchStatus ?? ''} onChange={e => set({ lunchStatus: e.target.value })} className={inputClass} />
          </Field>
        </div>
      </div>

      {/* ── Records ──────────────────────────────────────────────────────── */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <div className="flex items-start justify-between mb-4 gap-4">
          <div>
            <h2 className="text-lg font-semibold text-lgs-blue">Assessment Records</h2>
            <p className="text-slate-500 text-sm mt-0.5">
              ELA and Math are tiered independently, each needing at least {minDataPoints} weighted
              record{minDataPoints === 1 ? '' : 's'}. A subject with fewer stays Pending / Review.
            </p>
          </div>
          <button
            type="button"
            onClick={() => setRecords(prev => [...prev, { ...EMPTY_RECORD }])}
            className="shrink-0 flex items-center gap-1 px-3 py-1.5 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark transition-colors"
          >
            <Plus className="w-4 h-4" />
            Add Record
          </button>
        </div>

        {records.length === 0 ? (
          <p className="text-slate-500 text-sm">
            No records yet. You can save the student now and add records from their profile later —
            both tiers will read Pending until there is enough evidence.
          </p>
        ) : (
          <div className="space-y-3">
            {records.map((record, i) => (
              <AssessmentRecordFields
                key={i}
                index={i}
                value={record}
                ruleset={ruleset}
                onChange={next => setRecords(prev => prev.map((r, j) => (j === i ? next : r)))}
                onRemove={() => setRecords(prev => prev.filter((_, j) => j !== i))}
              />
            ))}
            <p className="text-xs text-slate-500">
              {weightedRecords} of {records.length} record(s) carry both a period and a performance
              level, so only those can count toward a tier.
            </p>
          </div>
        )}
      </div>

      {duplicate && (
        <div className="bg-amber-50 border border-amber-200 rounded-xl p-4 flex items-start gap-3">
          <AlertTriangle className="w-5 h-5 text-amber-600 shrink-0 mt-0.5" />
          <div className="text-sm">
            <p className="text-amber-900 font-medium">{duplicate}</p>
            <button
              onClick={() => save(true)}
              disabled={saving}
              className="mt-2 px-3 py-1.5 bg-amber-600 text-white text-xs font-medium rounded-lg hover:bg-amber-700 disabled:opacity-50"
            >
              Create a second record anyway
            </button>
          </div>
        </div>
      )}

      {error && <p className="text-sm text-red-600">{error}</p>}

      <div className="flex justify-end gap-3 pb-4">
        <button
          onClick={() => navigate('/students')}
          className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors"
        >
          Cancel
        </button>
        <button
          onClick={() => save(false)}
          disabled={saving || !form.fullName?.trim()}
          className="flex items-center gap-2 px-5 py-2 bg-lgs-red text-white font-medium rounded-lg hover:bg-lgs-red-dark disabled:opacity-50 transition-colors"
        >
          <Sparkles className="w-4 h-4" />
          {saving ? 'Saving…' : 'Save & Run Tiering'}
        </button>
      </div>
    </div>
  );
}
