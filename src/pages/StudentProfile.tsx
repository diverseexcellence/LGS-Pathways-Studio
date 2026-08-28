import React, { useState, useEffect, useMemo, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import { useParams, useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { studentsApi, assessmentsApi, aiApi, studentAuditApi, notesApi, configApi, Student, Assessment, AISummary, AuditEntry, CollaborationNote, TierRuleset } from '../lib/api';

const AUDIT_EVENT_LABELS: Record<string, string> = {
  TierRecommendation: 'Tier Recommendation',
  Edit: 'Profile Edit',
  View: 'Profile Viewed',
  Upload: 'Data Upload',
  AI: 'AI Summary Generated',
  Delete: 'Record Deleted',
  Login: 'Login',
  Error: 'System Error',
};
import { User, BookOpen, Clock, AlertTriangle, CheckCircle, MessageSquare, Info, Trash2, ArrowUpDown, ArrowUp, ArrowDown, ClipboardList, Plus, X, Sparkles } from 'lucide-react';

const MTSS_STRATEGIES: Record<string, string[]> = {
  "Tier 1": [
    "Differentiated Core Instruction",
    "Universal Behavior Support (PBIS)",
    "Flexible Grouping",
    "Standard Accommodations"
  ],
  "Tier 2": [
    "Small Group Targeted Reading Intervention",
    "Small Group Targeted Math Intervention",
    "Check-In/Check-Out (CICO) Behavior Support",
    "Social Skills Group",
    "Bi-weekly Progress Monitoring"
  ],
  "Tier 3": [
    "Intensive 1:1 Reading Intervention",
    "Intensive 1:1 Math Intervention",
    "Individualized Behavior Intervention Plan (BIP)",
    "Weekly Progress Monitoring",
    "Wrap-around Services"
  ]
};

function getAssessmentDisplayData(a: Assessment) {
  const subject = normalizeSubject(a.subject || 'Mixed');
  const proficiency = normalizeProficiency(a.proficiency || 'N/A');
  const formattedDate = formatDate(a.date ?? '');
  // Kept alongside the display string so sorting compares actual dates, not the
  // locale-formatted text — different sources (Acadience "22/8/2025" vs IXL
  // "(11/13/2025)") don't share a display format, so string/native-Date sort
  // on formattedDate silently breaks. See QA issue #9.
  const dateValue = parseFlexibleDate(a.date ?? '');

  return {
    type: a.uploadType || 'Assessment',
    formattedDate,
    dateValue,
    subject,
    proficiency,
    score: a.score != null ? String(a.score) : '',
    period: a.period ?? '',
  };
}

function normalizeSubject(s: string) {
  if (/ELA|English|Language/i.test(s)) return 'ELA';
  if (/Math/i.test(s)) return 'Math';
  return s;
}

function normalizeProficiency(p: string) {
  const l = p.toLowerCase().trim();
  // Already-normalised labels from backend — pass through as-is
  if (l === 'below proficiency') return 'Below Proficiency';
  if (l === 'approaching proficiency') return 'Approaching Proficiency';
  if (l === 'above proficiency') return 'Above Proficiency';
  if (l === 'at proficiency') return 'At Proficiency';
  // Keyword matching for raw values that bypass normalisation
  if (l.includes('far below') || l.includes('did not pass') || l === 'fail' || l === 'f' || l === 'not passed') return 'Below Proficiency';
  if (l.includes('below')) return 'Below Proficiency';
  if (l.includes('approaching')) return 'Approaching Proficiency';
  if (l.includes('above') || l.includes('exceeds')) return 'Above Proficiency';
  if (l.includes('at prof') || l === 'at' || l === 'proficient' || l === 'meets' ||
      l === 'passed' || l === 'pass' || l === 'p') return 'At Proficiency';
  // I-Read raw "Yes" = passed the I-Read test = At Proficiency
  if (l === 'yes') return 'At Proficiency';
  // I-Read raw "No" = did not pass = Below Proficiency
  if (l === 'no') return 'Below Proficiency';
  if (l === 'waived' || l === 'exempt') return p;
  return p;
}

// Parses date strings from mixed assessment sources into a timestamp.
// Source formats seen: IXL "(11/13/2025)" (US, month-first), Acadience
// "22/8/2025" (day-first — 22 can't be a month, so this disambiguates
// reliably whenever the first segment exceeds 12).
function parseFlexibleDate(d: string): number {
  const cleaned = (d || '').trim().replace(/^\(|\)$/g, '');
  if (!cleaned) return NaN;

  const parts = cleaned.split(/[/\-]/).map(p => p.trim());
  if (parts.length === 3 && parts.every(p => /^\d+$/.test(p))) {
    const [a, b, y] = parts.map(Number);
    const year = y < 100 ? 2000 + y : y;
    const [month, day] = a > 12 ? [b, a] : [a, b];
    const ts = new Date(year, month - 1, day).getTime();
    if (!isNaN(ts)) return ts;
  }

  const native = new Date(cleaned).getTime();
  return isNaN(native) ? NaN : native;
}

// "-1" = Kindergarten — confirmed by LGS (Velvet Wright) on the 2026-08-14 client demo call.
function normalizeGradeLabel(raw?: string | null): string {
  if (!raw) return '';
  const cleaned = String(raw).trim().toUpperCase();
  if (cleaned === 'K' || cleaned === 'KG' || cleaned === 'KINDERGARTEN' || cleaned === '0' || cleaned === '-1') return 'K';
  return cleaned.replace(/^0+(?=\d)/, '');
}

function formatDate(d: string) {
  const ts = parseFlexibleDate(d);
  if (!isNaN(ts)) return new Date(ts).toLocaleDateString();
  return d || 'N/A';
}

const ETHNICITY_MAP: Record<string, string> = {
  '1': 'American Indian or Alaska Native',
  '2': 'Asian',
  '3': 'Black or African American',
  '4': 'Hispanic or Latino',
  '5': 'Native Hawaiian or Pacific Islander',
  '6': 'White',
  '7': 'Two or More Races',
  'W': 'White',
  'B': 'Black or African American',
  'H': 'Hispanic or Latino',
  'A': 'Asian',
  'I': 'American Indian or Alaska Native',
  'P': 'Native Hawaiian or Pacific Islander',
  'M': 'Two or More Races',
  'X': 'Two or More Races',
};

function translateEthnicity(code: string | undefined) {
  if (!code || code === 'N/A') return 'N/A';
  return ETHNICITY_MAP[code.trim().toUpperCase()] ?? ETHNICITY_MAP[code.trim()] ?? code;
}

function formatDisplayName(fullName: string) {
  if (!fullName) return fullName;
  const parts = fullName.trim().split(/\s+/);
  if (parts.length < 2) return fullName;
  const last = parts[parts.length - 1];
  const first = parts.slice(0, parts.length - 1).join(' ');
  return `${last}, ${first}`;
}

function toYesNo(value: string | undefined, defaultVal = 'No') {
  if (!value || value.trim() === '' || value === 'N/A') return defaultVal;
  const v = value.trim().toLowerCase();
  if (v === 'false' || v === '0' || v === 'no') return 'No';
  return 'Yes';
}

function calculateAge(dob: string) {
  if (!dob || dob === 'N/A') return 'N/A';
  const d = new Date(dob);
  if (isNaN(d.getTime())) return 'N/A';
  return Math.abs(new Date(Date.now() - d.getTime()).getUTCFullYear() - 1970);
}

export default function StudentProfile() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { user } = useAuth();

  const [student, setStudent] = useState<Student | null>(null);
  const [assessments, setAssessments] = useState<Assessment[]>([]);
  const [aiSummary, setAiSummary] = useState<AISummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Two independent subjects — there is no combined overall tier to override (TR-011).
  const [overrideTierEla, setOverrideTierEla] = useState('');
  const [overrideTierMath, setOverrideTierMath] = useState('');
  const [isSavingTier, setIsSavingTier] = useState<'ela' | 'math' | null>(null);
  const [isGeneratingAI, setIsGeneratingAI] = useState(false);

  const [showPlanModal, setShowPlanModal] = useState(false);
  const [newPlan, setNewPlan] = useState({ tier: 'Tier 1', strategy: '', customDetails: '', frequency: 'Weekly' });

  const [showDemographics, setShowDemographics] = useState(false);
  const [selectedAssessment, setSelectedAssessment] = useState<Assessment | null>(null);

  const [assessmentSortConfig, setAssessmentSortConfig] = useState<{ key: string; direction: 'asc' | 'desc' } | null>(null);

  // G1 – Audit Trail
  const [auditEntries, setAuditEntries] = useState<AuditEntry[]>([]);
  const [auditLoading, setAuditLoading] = useState(false);

  // G2 – Collaboration Notes
  const [notes, setNotes] = useState<CollaborationNote[]>([]);
  const [noteText, setNoteText] = useState('');
  const [isPostingNote, setIsPostingNote] = useState(false);

  // G6 – tier criteria tooltip, built from the live ruleset so it can never drift from the
  // engine's actual weights/thresholds the way the old hardcoded boolean-rule text could.
  const [showTierTooltip, setShowTierTooltip] = useState(false);
  const [tierRuleset, setTierRuleset] = useState<TierRuleset | null>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    configApi.getTierRules().then(setTierRuleset).catch(() => {});
  }, []);

  // BRD ST-16 – Generate Recommendation
  const [isGeneratingRec, setIsGeneratingRec] = useState(false);

  const studentId = id ?? '';

  useEffect(() => {
    if (!studentId) return;
    load();
  }, [studentId]);

  async function load() {
    setLoading(true);
    setError('');
    try {
      const [s, a, ai, auditResult, notesList] = await Promise.all([
        studentsApi.get(studentId),
        assessmentsApi.byStudent(studentId),
        aiApi.get(studentId).catch(() => null),
        studentAuditApi.list(studentId).catch(() => ({ items: [], total: 0, page: 1, pageSize: 50 })),
        notesApi.list(studentId).catch(() => [] as CollaborationNote[]),
      ]);
      setStudent(s);
      setAssessments(a);
      setAiSummary(ai);
      setAuditEntries(auditResult.items);
      setNotes(notesList);
    } catch (e: any) {
      setError(e.message || 'Failed to load student data');
    } finally {
      setLoading(false);
    }
  }

  async function handlePostNote() {
    if (!noteText.trim()) return;
    setIsPostingNote(true);
    try {
      const note = await notesApi.create(studentId, noteText.trim());
      setNotes(prev => [note, ...prev]);
      setNoteText('');
    } catch (e: any) {
      alert('Failed to save note: ' + e.message);
    } finally {
      setIsPostingNote(false);
    }
  }

  async function handleDeleteNote(noteId: string) {
    if (!confirm('Delete this note?')) return;
    try {
      await notesApi.delete(studentId, noteId);
      setNotes(prev => prev.filter(n => n.id !== noteId));
    } catch (e: any) {
      alert('Failed to delete note: ' + e.message);
    }
  }

  function formatAuditTimestamp(ts: string) {
    try {
      return new Date(ts).toLocaleString('en-US', {
        month: '2-digit', day: '2-digit', year: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit',
      });
    } catch { return ts; }
  }

  async function handleOverrideTier(subject: 'ela' | 'math') {
    const value = subject === 'ela' ? overrideTierEla : overrideTierMath;
    if (!value || !student) return;
    setIsSavingTier(subject);
    try {
      const updated = await studentsApi.setSubjectTier(studentId, subject, { tier: value, status: 'Finalized' });
      setStudent(updated);
      if (subject === 'ela') setOverrideTierEla(''); else setOverrideTierMath('');
    } catch (e: any) {
      alert('Failed to save tier: ' + e.message);
    } finally {
      setIsSavingTier(null);
    }
  }

  async function handleGenerateRecommendation() {
    setIsGeneratingRec(true);
    try {
      const updated = await studentsApi.recalculateTier(studentId);
      setStudent(updated);
      // Refresh audit trail to show the new recommendation entry
      const auditResult = await studentAuditApi.list(studentId).catch(() => ({ items: [], total: 0, page: 1, pageSize: 50 }));
      setAuditEntries(auditResult.items);
    } catch (e: any) {
      alert(e.message || 'Tier calculation failed.');
    } finally {
      setIsGeneratingRec(false);
    }
  }

  async function handleGenerateAI() {
    setIsGeneratingAI(true);
    try {
      const summary = await aiApi.generate(studentId);
      setAiSummary(summary);
    } catch (e: any) {
      alert(e.message || 'AI summary generation failed.');
    } finally {
      setIsGeneratingAI(false);
    }
  }

  const sortedAssessments = useMemo(() => {
    const rows = assessments.map(a => ({ ...a, displayData: getAssessmentDisplayData(a) }));
    if (!assessmentSortConfig) return rows;
    return [...rows].sort((a, b) => {
      let av: any = a.displayData[assessmentSortConfig.key as keyof typeof a.displayData] ?? '';
      let bv: any = b.displayData[assessmentSortConfig.key as keyof typeof b.displayData] ?? '';
      if (assessmentSortConfig.key === 'score') {
        av = parseFloat(av) || 0;
        bv = parseFloat(bv) || 0;
      } else if (assessmentSortConfig.key === 'formattedDate') {
        av = a.displayData.dateValue;
        bv = b.displayData.dateValue;
        av = isNaN(av) ? -Infinity : av;
        bv = isNaN(bv) ? -Infinity : bv;
      }
      return av < bv
        ? assessmentSortConfig.direction === 'asc' ? -1 : 1
        : av > bv
        ? assessmentSortConfig.direction === 'asc' ? 1 : -1
        : 0;
    });
  }, [assessments, assessmentSortConfig]);

  function requestSort(key: string) {
    setAssessmentSortConfig(prev =>
      prev?.key === key && prev.direction === 'asc'
        ? { key, direction: 'desc' }
        : { key, direction: 'asc' }
    );
  }

  function SortIcon({ colKey }: { colKey: string }) {
    if (!assessmentSortConfig || assessmentSortConfig.key !== colKey)
      return <ArrowUpDown className="w-4 h-4 ml-1 text-slate-400" />;
    return assessmentSortConfig.direction === 'asc'
      ? <ArrowUp className="w-4 h-4 ml-1 text-lgs-blue" />
      : <ArrowDown className="w-4 h-4 ml-1 text-lgs-blue" />;
  }

  function tierBadgeColor(tier: string | null | undefined) {
    return tier === 'Tier 1' ? 'bg-green-100 text-green-700 border-green-200' :
      tier === 'Tier 2' ? 'bg-yellow-100 text-yellow-700 border-yellow-200' :
      tier === 'Tier 3' ? 'bg-red-100 text-red-700 border-red-200' :
      'bg-slate-100 text-slate-600 border-slate-200';
  }

  function pendingReasonText(reason: string | null | undefined) {
    return reason === 'no_assessments' ? 'No assessment data uploaded yet.'
      : reason === 'insufficient_data_points' ? 'Not enough evidence yet — at least 2 data points are required.'
      : reason === 'all_evidence_excluded' ? 'Assessment data present but none of it is usable evidence (see Tiering Evidence below).'
      : reason || 'Pending / Review — not enough evidence for an automatic tier.';
  }

  // The overall hero accent is a colour cue only — never labelled or stored — taken from
  // whichever subject has the lower (more urgent) tier. There is no combined tier value (TR-011).
  const worstTier = [student?.elaTier?.tier, student?.mathTier?.tier].includes('Tier 3') ? 'Tier 3'
    : [student?.elaTier?.tier, student?.mathTier?.tier].includes('Tier 2') ? 'Tier 2'
    : [student?.elaTier?.tier, student?.mathTier?.tier].includes('Tier 1') ? 'Tier 1'
    : null;
  const tierAccent =
    worstTier === 'Tier 1' ? 'border-t-green-500' :
    worstTier === 'Tier 2' ? 'border-t-yellow-500' :
    worstTier === 'Tier 3' ? 'border-t-red-500' :
    'border-t-lgs-red';

  if (loading) return (
    <div className="flex items-center justify-center min-h-64">
      <div className="text-center">
        <div className="w-8 h-8 border-2 border-lgs-blue border-t-transparent rounded-full animate-spin mx-auto mb-3" />
        <p className="text-slate-500 text-sm">Loading student profile…</p>
      </div>
    </div>
  );
  if (error) return <div className="p-8 text-red-600">{error}</div>;
  if (!student) return <div className="p-8">Student not found.</div>;

  return (
    <div className="space-y-6 max-w-6xl mx-auto">

      {/* Back link */}
      <button
        onClick={() => navigate('/students')}
        className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-lgs-blue transition-colors"
      >
        ← Back to Students List
      </button>

      {/* ── Hero Card ─────────────────────────────────────────────────────── */}
      <div className={`bg-white rounded-xl shadow-sm border border-slate-200 border-t-4 ${tierAccent} overflow-hidden`}>
        {/* Top bar: name + tier badge */}
        <div className="px-6 pt-6 pb-4 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
          <div className="flex items-center gap-4">
            {/* Avatar circle */}
            <div className="w-14 h-14 rounded-full bg-lgs-blue flex items-center justify-center shrink-0 shadow-sm">
              <span className="text-white text-xl font-bold select-none">
                {student.fullName?.trim().split(/\s+/).map(p => p[0]).slice(0, 2).join('').toUpperCase()}
              </span>
            </div>
            <div>
              <h1 className="text-2xl font-bold text-lgs-blue leading-tight">
                {formatDisplayName(student.fullName)}
              </h1>
              <div className="flex flex-wrap items-center gap-2 mt-1">
                {student.stn && (
                  <span className="text-xs text-slate-500 font-mono bg-slate-100 px-2 py-0.5 rounded">
                    STN {student.stn}
                  </span>
                )}
                <span className="text-xs text-slate-400">Grade {normalizeGradeLabel(student.grade) || '—'}</span>
                <span className="text-slate-300 text-xs">•</span>
                <span className="text-xs text-slate-400">{student.classGroup || '—'}</span>
                {student.homeRoom && (
                  <>
                    <span className="text-slate-300 text-xs">•</span>
                    <span className="text-xs text-slate-400">Room {student.homeRoom}</span>
                  </>
                )}
              </div>
            </div>
          </div>

          {/* Two independent subject tier badges — no combined overall tier (TR-011) */}
          <div className="shrink-0 flex flex-col sm:flex-row gap-4">
            {([['ELA', student.elaTier], ['Math', student.mathTier]] as const).map(([label, t]) => (
              <div key={label} className="text-right">
                <p className="text-xs font-bold text-slate-400 uppercase tracking-wide mb-1">{label}</p>
                <span className={`inline-flex items-center gap-1.5 px-4 py-1.5 rounded-full text-sm font-semibold border ${tierBadgeColor(t?.tier)}`}>
                  {t?.tier || 'Pending'}
                </span>
                {t?.status && t.status !== 'Pending' && (
                  <p className="text-xs text-slate-400 mt-1">
                    {t.status}{t.score != null ? ` · score ${t.score.toFixed(2)} (${t.dataPoints} pt${t.dataPoints === 1 ? '' : 's'})` : ''}
                  </p>
                )}
                {t?.status === 'Pending' && (
                  <p className="text-xs text-amber-600 mt-0.5 max-w-[180px]">{pendingReasonText(t.pendingReason)}</p>
                )}
              </div>
            ))}
          </div>
        </div>

        {/* Divider */}
        <div className="border-t border-slate-100 mx-6" />

        {/* Demographic grid */}
        <div className="px-6 py-4 grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-x-6 gap-y-4 text-sm">
          {[
            { label: 'Date of Birth', value: student.dob ? new Date(student.dob).toLocaleDateString() : 'N/A' },
            { label: 'Age', value: String(calculateAge(student.dob)) },
            { label: 'Gender', value: student.gender || 'N/A' },
            { label: 'Ethnicity', value: translateEthnicity(student.ethnicity) },
            { label: 'EL Status', value: toYesNo(student.ellStatus) },
            { label: 'Sp. Education', value: toYesNo(student.spedStatus) },
            { label: '504 Plan', value: toYesNo(student.section504, 'No') },
            { label: 'Lunch', value: student.lunchStatus || 'N/A' },
          ].map(({ label, value }) => (
            <div key={label}>
              <p className="text-xs font-medium text-slate-400 uppercase tracking-wide mb-0.5">{label}</p>
              <p className="text-slate-800 font-medium truncate" title={value}>{value}</p>
            </div>
          ))}
        </div>

        {/* Footer: source line + entry/exit + demographics link (BRD §8.3.2) */}
        <div className="px-6 pb-4 flex flex-wrap items-center gap-4 text-xs text-slate-400">
          {student.fileName && (
            <span>Source: <span className="text-slate-600 font-mono">{student.fileName}</span></span>
          )}
          {student.entryDate && (
            <span>Entry: <span className="text-slate-600">{new Date(student.entryDate).toLocaleDateString()}</span></span>
          )}
          {student.exitDate && (
            <span>Exit: <span className="text-slate-600">{new Date(student.exitDate).toLocaleDateString()}</span></span>
          )}
          <button
            onClick={() => setShowDemographics(true)}
            className="ml-auto text-lgs-red hover:underline font-medium text-xs"
          >
            View All Demographics →
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left: Assessments + AI Summary */}
        <div className="lg:col-span-2 space-y-6">
          {/* Assessments */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
              <BookOpen className="w-5 h-5 text-lgs-red" />
              Academic Assessments
            </h2>
            {assessments.length === 0 ? (
              <p className="text-slate-500 text-sm">No assessment data available.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm text-left">
                  <thead className="bg-slate-50 text-slate-600 font-medium border-b border-slate-200 select-none">
                    <tr>
                      {[
                        { label: 'Date', key: 'formattedDate' },
                        { label: 'Type', key: 'type' },
                        { label: 'Subject', key: 'subject' },
                        { label: 'Period', key: 'period' },
                        { label: 'Score', key: 'score' },
                        { label: 'Proficiency', key: 'proficiency' },
                      ].map(col => (
                        <th key={col.key} className="px-4 py-3 cursor-pointer hover:bg-slate-100" onClick={() => requestSort(col.key)}>
                          <div className="flex items-center">{col.label}<SortIcon colKey={col.key} /></div>
                        </th>
                      ))}
                      <th className="px-4 py-3 text-right">Details</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {sortedAssessments.map(a => {
                      const d = a.displayData;
                      return (
                        <tr key={a.id} className="hover:bg-slate-50">
                          <td className="px-4 py-3">{d.formattedDate}</td>
                          <td className="px-4 py-3">{d.type}</td>
                          <td className="px-4 py-3">{d.subject}</td>
                          <td className="px-4 py-3">{d.period}</td>
                          <td className="px-4 py-3 font-medium">{d.score}</td>
                          <td className="px-4 py-3">
                            <span className={`px-2 py-1 rounded-full text-xs font-medium ${
                              d.proficiency.includes('Below') ? 'bg-red-100 text-red-700' :
                              d.proficiency.includes('Approaching') ? 'bg-yellow-100 text-yellow-700' :
                              d.proficiency.includes('At') || d.proficiency.includes('Above') ? 'bg-green-100 text-green-700' :
                              'bg-slate-100 text-slate-700'
                            }`}>{d.proficiency}</span>
                          </td>
                          <td className="px-4 py-3 text-right">
                            <button onClick={() => setSelectedAssessment(a)} className="text-lgs-blue hover:underline text-xs font-medium" title="View Details">
                              Details
                            </button>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          {/* AI Summary */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <div className="flex justify-between items-center mb-4">
              <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2">
                <Sparkles className="w-5 h-5 text-lgs-red" />
                AI Progress Summary
              </h2>
              <div className="flex items-center gap-2">
                {/* G6: Tier rules tooltip */}
                <div className="relative" ref={tooltipRef}>
                  <button
                    onMouseEnter={() => setShowTierTooltip(true)}
                    onMouseLeave={() => setShowTierTooltip(false)}
                    className="p-1.5 rounded-lg text-slate-400 hover:text-lgs-blue hover:bg-slate-100 transition-colors"
                    title="Tiering Criteria"
                  >
                    <Info className="w-4 h-4" />
                  </button>
                  {showTierTooltip && (
                    <div className="absolute right-0 top-8 z-20 w-72 bg-white border border-slate-200 rounded-lg shadow-lg p-3 text-xs text-slate-700">
                      <p className="font-semibold text-slate-800 mb-1.5">Tiering Criteria</p>
                      {tierRuleset ? (
                        <>
                          <p className="text-slate-500 mb-1.5">ELA and Math are scored independently: weighted score = Σ(performance value × evidence weight) ÷ Σ(available weight).</p>
                          {[...tierRuleset.tierThresholds].sort((a, b) => b.minScoreInclusive - a.minScoreInclusive).map(t => (
                            <p key={t.tier} className="mt-0.5">
                              <span className={`font-medium ${t.tier === 'Tier 1' ? 'text-green-600' : t.tier === 'Tier 2' ? 'text-yellow-600' : 'text-red-600'}`}>{t.tier}:</span>{' '}
                              score ≥ {t.minScoreInclusive.toFixed(2)}
                            </p>
                          ))}
                          <p className="mt-1.5 text-slate-500">Requires at least {tierRuleset.minDataPoints} data point{tierRuleset.minDataPoints === 1 ? '' : 's'}; otherwise the subject is Pending / Review.</p>
                        </>
                      ) : (
                        <>
                          <p><span className="text-green-600 font-medium">Tier 1:</span> weighted score ≥ 2.00</p>
                          <p className="mt-1"><span className="text-yellow-600 font-medium">Tier 2:</span> weighted score 1.00–1.99</p>
                          <p className="mt-1"><span className="text-red-600 font-medium">Tier 3:</span> weighted score below 1.00</p>
                        </>
                      )}
                    </div>
                  )}
                </div>
                <button
                  onClick={handleGenerateAI}
                  disabled={isGeneratingAI}
                  className="flex items-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
                >
                  {isGeneratingAI ? 'Generating...' : aiSummary ? 'Regenerate' : 'Generate AI Summary'}
                </button>
              </div>
            </div>
            {aiSummary ? (
              <div className="bg-slate-50 border border-slate-200 rounded-lg p-4">
                <div className="text-sm text-slate-800 leading-relaxed prose prose-sm max-w-none
                  prose-headings:text-slate-800 prose-headings:font-semibold
                  prose-h2:text-base prose-h2:mt-2 prose-h2:mb-1
                  prose-h3:text-sm prose-h3:mt-3 prose-h3:mb-1
                  prose-ul:my-1 prose-li:my-0.5
                  prose-p:my-1">
                  <ReactMarkdown>
                    {(() => {
                      const firstName = student?.fullName?.trim().split(/\s+/)[0] ?? 'The student';
                      return aiSummary.summaryText
                        // Strip the redundant top-level heading the LLM emits from the prompt template
                        .replace(/^##\s+AI Assistant Summary\s*\n?/im, '')
                        .replace(/\bStudent\s+S-[A-Za-z0-9-]+/gi, firstName)
                        .replace(/\bS-[A-Za-z0-9-]+\b/gi, firstName);
                    })()}
                  </ReactMarkdown>
                </div>
                <p className="text-xs text-slate-400 mt-3">Generated: {new Date(aiSummary.generatedAt).toLocaleString()}</p>
              </div>
            ) : (
              <p className="text-slate-500 text-sm">No AI summary yet. Click Generate to create one (PII-free).</p>
            )}
          </div>
        </div>

        {/* Right: Tier Management + Audit Trail */}
        <div className="space-y-6">
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200 border-t-4 border-t-lgs-blue">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4">Tier Management</h2>

            {/* BRD ST-16: one Generate Recommendation button recomputes both subjects at once —
                the engine skips whichever subject is already Finalized. Override/Finalize is
                per-subject below since ELA and Math are independent (TR-011). */}
            <div className="mb-4">
              <button
                onClick={handleGenerateRecommendation}
                disabled={isGeneratingRec || (student?.elaTier.status === 'Finalized' && student?.mathTier.status === 'Finalized')}
                className="w-full flex items-center justify-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
                title={student?.elaTier.status === 'Finalized' && student?.mathTier.status === 'Finalized' ? 'Both tiers are Finalized — use Override to change' : 'Run the tier recommendation engine for this student'}
              >
                <Sparkles className="w-4 h-4" />
                {isGeneratingRec ? 'Calculating…' : 'Generate Recommendation'}
              </button>
            </div>

            {([
              ['ela', 'ELA', student?.elaTier, overrideTierEla, setOverrideTierEla] as const,
              ['math', 'Math', student?.mathTier, overrideTierMath, setOverrideTierMath] as const,
            ]).map(([subject, label, t, value, setValue]) => (
              <div key={subject} className="border-t border-slate-100 pt-4 mt-4 first:mt-0 first:border-t-0 first:pt-0">
                <label className="block text-sm font-medium text-slate-700 mb-2">
                  {label} — Override / Finalize
                  {t?.status === 'Finalized' && <span className="ml-2 text-xs font-normal text-slate-400">(Finalized — Generate Recommendation won't overwrite this)</span>}
                </label>
                <div className="flex gap-2">
                  <select
                    value={value}
                    onChange={e => setValue(e.target.value)}
                    className="flex-1 px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none"
                  >
                    <option value="">Select Tier...</option>
                    <option value="Tier 1">Tier 1</option>
                    <option value="Tier 2">Tier 2</option>
                    <option value="Tier 3">Tier 3</option>
                  </select>
                  <button
                    onClick={() => handleOverrideTier(subject)}
                    disabled={!value || isSavingTier === subject}
                    className="px-4 py-2 bg-lgs-red text-white text-sm font-medium rounded-lg hover:bg-lgs-red-dark disabled:opacity-50"
                  >
                    {isSavingTier === subject ? '...' : 'Save'}
                  </button>
                </div>
                {t?.reasoning && (
                  <details className="mt-2">
                    <summary className="text-xs text-slate-400 cursor-pointer hover:text-lgs-blue">Tiering Evidence</summary>
                    <p className="text-xs text-slate-500 mt-1.5 leading-relaxed">{t.reasoning}</p>
                    {t.evidence.length > 0 && (
                      <ul className="mt-1.5 space-y-0.5">
                        {t.evidence.map(ev => (
                          <li key={ev.assessmentId} className={`text-xs ${ev.counted ? 'text-slate-600' : 'text-slate-400 line-through'}`}>
                            {ev.source} {ev.period ?? '—'} "{ev.category ?? 'n/a'}"
                            {ev.counted ? ` (${ev.value}×${ev.weight})` : ` (excluded: ${ev.exclusionReason})`}
                          </li>
                        ))}
                      </ul>
                    )}
                  </details>
                )}
              </div>
            ))}
          </div>

          {/* Learning Plans stub - local state only (future: dedicated API endpoint) */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <div className="flex justify-between items-center mb-4">
              <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2">
                <ClipboardList className="w-5 h-5 text-lgs-red" />
                MTSS Learning Plans
              </h2>
              <button
                onClick={() => {
                  // Default to whichever subject has the more urgent (lower) tier, since the
                  // student no longer has one combined tier to default from (TR-011).
                  const candidates = [student?.elaTier?.tier, student?.mathTier?.tier];
                  const validTier = candidates.includes('Tier 3') ? 'Tier 3'
                    : candidates.includes('Tier 2') ? 'Tier 2'
                    : candidates.includes('Tier 1') ? 'Tier 1'
                    : 'Tier 1';
                  setNewPlan({ tier: validTier, strategy: '', customDetails: '', frequency: 'Weekly' });
                  setShowPlanModal(true);
                }}
                className="flex items-center gap-1 px-3 py-1.5 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark transition-colors"
              >
                <Plus className="w-4 h-4" />
                Add
              </button>
            </div>
            <p className="text-slate-500 text-sm">Learning plans are stored locally in this session. A dedicated API endpoint will persist them in a future release.</p>
          </div>

          {/* G1 – Audit Trail (BRD ST-21) */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
              <Clock className="w-5 h-5 text-lgs-red" />
              Audit Trail
            </h2>
            {auditEntries.length === 0 ? (
              <p className="text-slate-500 text-sm">No audit events recorded yet.</p>
            ) : (
              <ul className="space-y-2 max-h-64 overflow-y-auto">
                {auditEntries.map(e => (
                  <li key={e.id} className="text-xs border-b border-slate-100 pb-2 last:border-0 last:pb-0">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className="font-semibold text-slate-800">
                        {AUDIT_EVENT_LABELS[e.eventType] ?? e.eventType}
                      </span>
                      <span className="text-slate-400">{formatAuditTimestamp(e.timestamp)}</span>
                    </div>
                    <p className="text-slate-500 mt-0.5">{e.adminEmail}</p>
                    {e.details && <p className="text-slate-600 mt-0.5 leading-snug">{e.details}</p>}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      </div>

      {/* G2 – Collaboration Notes (BRD ST-20) — full-width below the two-column grid */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
          <MessageSquare className="w-5 h-5 text-lgs-red" />
          Collaboration Notes
        </h2>
        <div className="flex gap-2 mb-4">
          <textarea
            value={noteText}
            onChange={e => setNoteText(e.target.value)}
            rows={2}
            placeholder="Add a collaboration note…"
            className="flex-1 px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none resize-none"
          />
          <button
            onClick={handlePostNote}
            disabled={isPostingNote || !noteText.trim()}
            className="px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 self-start mt-0"
          >
            {isPostingNote ? '...' : 'Post'}
          </button>
        </div>
        {notes.length === 0 ? (
          <p className="text-slate-500 text-sm">No collaboration notes yet.</p>
        ) : (
          <ul className="space-y-3 max-h-72 overflow-y-auto">
            {notes.map(n => (
              <li key={n.id} className="flex items-start gap-3 bg-slate-50 rounded-lg p-3 border border-slate-100">
                <div className="flex-1 min-w-0">
                  <p className="text-sm text-slate-800 whitespace-pre-wrap break-words">{n.text}</p>
                  <p className="text-xs text-slate-400 mt-1">{n.createdBy} · {formatAuditTimestamp(n.createdAt)}</p>
                </div>
                <button
                  onClick={() => handleDeleteNote(n.id)}
                  className="shrink-0 text-slate-300 hover:text-red-500 transition-colors"
                  title="Delete note"
                >
                  <Trash2 className="w-4 h-4" />
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {/* Demographics Modal */}
      {showDemographics && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-xl w-full p-6 max-h-[90vh] overflow-y-auto">
            <h3 className="text-lg font-bold text-slate-900 mb-5">Full Demographics</h3>
            <div className="space-y-5 text-sm">

              {/* Student Identity */}
              <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wide mb-2">Student Identity</p>
                <div className="grid grid-cols-2 gap-x-6 gap-y-2">
                  {[
                    ['Full Name', formatDisplayName(student.fullName)],
                    ['STN', student.stn || 'N/A'],
                    ['Date of Birth', student.dob ? new Date(student.dob).toLocaleDateString() : 'N/A'],
                    ['Age', String(calculateAge(student.dob))],
                    ['Gender', student.gender || 'N/A'],
                    ['Ethnicity', translateEthnicity(student.ethnicity)],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <span className="block text-xs text-slate-400 font-medium">{label}</span>
                      <span className="text-slate-900">{value}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div className="border-t border-slate-100" />

              {/* Enrollment Information */}
              <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wide mb-2">Enrollment Information</p>
                <div className="grid grid-cols-2 gap-x-6 gap-y-2">
                  {[
                    ['Grade', normalizeGradeLabel(student.grade) || 'N/A'],
                    ['Class Group', student.classGroup || 'N/A'],
                    ['Homeroom', student.homeRoom || 'N/A'],
                    ['Entry Date', student.entryDate ? new Date(student.entryDate).toLocaleDateString() : 'N/A'],
                    ['Exit Date', student.exitDate ? new Date(student.exitDate).toLocaleDateString() : 'N/A'],
                    ['Enrolled', student.enrolDate ? new Date(student.enrolDate).toLocaleDateString() : 'N/A'],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <span className="block text-xs text-slate-400 font-medium">{label}</span>
                      <span className="text-slate-900">{value}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div className="border-t border-slate-100" />

              {/* Program & Support Indicators */}
              <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wide mb-2">Program & Support Indicators</p>
                <div className="grid grid-cols-2 gap-x-6 gap-y-2">
                  {[
                    ['Special Education', toYesNo(student.spedStatus)],
                    ['504 Plan', toYesNo(student.section504, 'No')],
                    ['Lunch Status', student.lunchStatus || 'N/A'],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <span className="block text-xs text-slate-400 font-medium">{label}</span>
                      <span className="text-slate-900">{value}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div className="border-t border-slate-100" />

              {/* EL / Language Details */}
              <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wide mb-2">EL / Language Details</p>
                <div className="grid grid-cols-2 gap-x-6 gap-y-2">
                  {[
                    ['EL Status', toYesNo(student.ellStatus)],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <span className="block text-xs text-slate-400 font-medium">{label}</span>
                      <span className="text-slate-900">{value}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div className="border-t border-slate-100" />

              {/* Source Reference */}
              <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wide mb-2">Source Reference</p>
                <div className="grid grid-cols-2 gap-x-6 gap-y-2">
                  {[
                    ['Source File', student.fileName || 'Unknown'],
                  ].map(([label, value]) => (
                    <div key={label}>
                      <span className="block text-xs text-slate-400 font-medium">{label}</span>
                      <span className="text-slate-900 font-mono text-xs break-all">{value}</span>
                    </div>
                  ))}
                </div>
              </div>

            </div>
            <div className="mt-6 flex justify-end">
              <button onClick={() => setShowDemographics(false)} className="px-4 py-2 bg-slate-100 text-slate-700 font-medium hover:bg-slate-200 rounded-lg transition-colors">
                Close
              </button>
            </div>
          </div>
        </div>
      )}

      {/* G5 – Enriched Assessment Details Modal */}
      {selectedAssessment && (() => {
        return (
          <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
            <div className="bg-white rounded-xl shadow-lg max-w-2xl w-full p-6 max-h-[90vh] overflow-y-auto">
              <div className="flex justify-between items-center mb-4">
                <h3 className="text-lg font-bold text-slate-900">Assessment Details</h3>
                <button onClick={() => setSelectedAssessment(null)} className="text-slate-400 hover:text-slate-600">
                  <X className="w-5 h-5" />
                </button>
              </div>

              {/* Core fields */}
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 bg-slate-50 p-4 rounded-lg border border-slate-100 text-sm mb-4">
                {[
                  ['Type', selectedAssessment.uploadType],
                  ['Subject', normalizeSubject(selectedAssessment.subject ?? '')],
                  ['Score', selectedAssessment.score ?? 'N/A'],
                  ['Proficiency', normalizeProficiency(selectedAssessment.proficiency ?? 'N/A')],
                  ['Period', selectedAssessment.period ?? 'N/A'],
                  ['Date', formatDate(selectedAssessment.date ?? '')],
                ].map(([label, value]) => (
                  <div key={String(label)}>
                    <span className="block text-xs text-slate-500 font-medium mb-0.5">{label}</span>
                    <span className="text-slate-900 font-medium">{String(value)}</span>
                  </div>
                ))}
              </div>


              <div className="mt-6 flex justify-end">
                <button onClick={() => setSelectedAssessment(null)} className="px-4 py-2 bg-slate-100 text-slate-700 font-medium hover:bg-slate-200 rounded-lg transition-colors">
                  Close
                </button>
              </div>
            </div>
          </div>
        );
      })()}

      {/* Learning Plan Modal */}
      {showPlanModal && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-lg w-full p-6">
            <div className="flex justify-between items-center mb-4">
              <h3 className="text-lg font-bold text-slate-900">Create Learning Plan</h3>
              <button onClick={() => setShowPlanModal(false)} className="text-slate-400 hover:text-slate-600">
                <X className="w-5 h-5" />
              </button>
            </div>
            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Target Tier</label>
                <select
                  value={newPlan.tier}
                  onChange={e => setNewPlan({ ...newPlan, tier: e.target.value, strategy: '' })}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
                >
                  <option>Tier 1</option>
                  <option>Tier 2</option>
                  <option>Tier 3</option>
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">MTSS/RTI Strategy</label>
                <select
                  value={newPlan.strategy}
                  onChange={e => setNewPlan({ ...newPlan, strategy: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
                >
                  <option value="">Select a strategy...</option>
                  {(MTSS_STRATEGIES[newPlan.tier] || []).map(s => (
                    <option key={s} value={s}>{s}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Frequency</label>
                <select
                  value={newPlan.frequency}
                  onChange={e => setNewPlan({ ...newPlan, frequency: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
                >
                  <option>Daily</option>
                  <option>Weekly</option>
                  <option>Bi-weekly</option>
                  <option>Monthly</option>
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Custom Details / Goals</label>
                <textarea
                  value={newPlan.customDetails}
                  onChange={e => setNewPlan({ ...newPlan, customDetails: e.target.value })}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
                  rows={3}
                  placeholder="Specific goals, materials, or notes..."
                />
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button onClick={() => setShowPlanModal(false)} className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg">Cancel</button>
                <button
                  disabled={!newPlan.strategy}
                  onClick={() => {
                    alert('Learning plan recorded locally. Persistence API coming soon.');
                    setShowPlanModal(false);
                  }}
                  className="px-4 py-2 bg-lgs-blue text-white font-medium hover:bg-lgs-blue-dark rounded-lg disabled:opacity-50"
                >
                  Create Plan
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
