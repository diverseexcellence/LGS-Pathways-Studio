import React, { useState, useEffect, useMemo, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import { useParams, useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { studentsApi, assessmentsApi, aiApi, studentAuditApi, notesApi, configApi, Student, Assessment, AssessmentInput, AISummary, AuditEntry, CollaborationNote, TierRuleset } from '../lib/api';
import { parseFlexibleDate, formatUsDate, formatUsDateTime } from '../lib/dates';
import AssessmentRecordFields, { EMPTY_RECORD, matchProficiencyOption } from '../components/AssessmentRecordFields';
import {
  ELL_OPTIONS, ENROLLMENT_OPTIONS, ETHNICITY_OPTIONS, GENDER_OPTIONS, LUNCH_OPTIONS,
  RACE_OPTIONS, TRUE_FALSE_OPTIONS, optionLabel, optionsWithCurrent,
} from '../lib/demographics';

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
import { User, BookOpen, Clock, AlertTriangle, CheckCircle, MessageSquare, Info, Trash2, ArrowUpDown, ArrowUp, ArrowDown, ClipboardList, Plus, X, Sparkles, Pencil } from 'lucide-react';

// The editable student fields, as form strings. Also the baseline the save diffs against, so both
// sides of that comparison are built the same way.
function openEditValues(student: Student): Record<string, string> {
  return {
    fullName: student.fullName ?? '',
    stn: student.stn ?? '',
    dob: student.dob ?? '',
    grade: student.grade ?? '',
    classGroup: student.classGroup ?? '',
    homeRoom: student.homeRoom ?? '',
    gender: student.gender ?? '',
    ethnicity: student.ethnicity ?? '',
    race: student.race ?? '',
    enrollment: student.isActive === false ? 'Unenrolled' : 'Enrolled',
    ellStatus: student.ellStatus ?? '',
    spedStatus: student.spedStatus ?? '',
    section504: student.section504 ?? '',
    lunchStatus: student.lunchStatus ?? '',
    entryDate: student.entryDate ?? '',
    exitDate: student.exitDate ?? '',
  };
}

// A stored assessment, back in the shape the entry form uses. The form offers the normalized
// period/level vocabularies, so an edit starts from the normalized values rather than the raw
// source text — re-saving a record must not undo the normalization it already went through.
function toRecordInput(a: Assessment): AssessmentInput {
  return {
    uploadType: a.uploadType,
    subject: a.subject ?? '',
    period: a.period ?? '',
    score: a.score ?? null,
    proficiency: a.proficiency ?? '',
    date: a.dateIso ?? '',
  };
}

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

function getAssessmentDisplayData(a: Assessment, ruleset: TierRuleset | null) {
  const subject = normalizeSubject(a.subject || 'Mixed');
  const rawProficiency = a.proficiency || 'N/A';
  const proficiency = matchProficiencyOption(a.uploadType, rawProficiency, ruleset) ?? normalizeProficiency(rawProficiency);
  const formattedDate = formatDate(a.date ?? '');
  // Kept alongside the display string so sorting compares actual dates, not the
  // locale-formatted text — different sources (Acadience "22/8/2025" vs IXL
  // "(11/13/2025)") don't share a display format, so string/native-Date sort
  // on formattedDate silently breaks. See QA issue #9.
  //
  // Prefer the backend's normalized dateIso: it was parsed with per-source knowledge
  // (Acadience is day-first, ILEARN/IXL month-first), which the local parser below has no
  // way to know. Falling back to it only when dateIso is absent keeps older records sortable.
  const isoValue = a.dateIso ? new Date(`${a.dateIso}T00:00:00`).getTime() : NaN;
  const dateValue = isNaN(isoValue) ? parseFlexibleDate(a.date ?? '') : isoValue;

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
  // IXL ("on grade") and Acadience ("at benchmark") keep their own wording. The keyword
  // rules below are the ILEARN bands; "above" inside "above grade" must not become
  // "Above Proficiency".
  if (/\bgrade\b/.test(l) || /\bbenchmark\b/.test(l)) {
    return l.replace(/\s+level$/, '').replace(/\b[a-z]/g, c => c.toUpperCase());
  }
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

// "-1" = Kindergarten — confirmed by LGS (Velvet Wright) on the 2026-08-14 client demo call.
function normalizeGradeLabel(raw?: string | null): string {
  if (!raw) return '';
  const cleaned = String(raw).trim().toUpperCase();
  if (cleaned === 'K' || cleaned === 'KG' || cleaned === 'KINDERGARTEN' || cleaned === '0' || cleaned === '-1') return 'K';
  return cleaned.replace(/^0+(?=\d)/, '');
}

// A tier set by a person. Accepts the legacy "Finalized" value that pre-rename student documents
// still carry, so an existing override keeps displaying (and keeps blocking recalculation).
function isAdminOverride(status?: string | null): boolean {
  return status === 'Admin Override' || status === 'Finalized';
}

// Why an assessment was left out of the weighted score. The engine stores machine tokens; these
// are the plain-English equivalents shown to staff, who need to know whether the omission is
// expected (a superseded duplicate) or a data problem they can fix (an unidentified period).
const EXCLUSION_LABELS: Record<string, string> = {
  unknown_period: 'the assessment period could not be identified, so it cannot be weighted',
  superseded: 'replaced by a more recent result for the same period',
  source_excluded: 'this source is not part of the weighted calculation',
  unrecognized_category: 'the proficiency level was not recognised',
  unknown_subject: 'the subject could not be identified as ELA or Math',
  no_result_reported: 'the source reported no result, so the assessment was not completed',
};

function exclusionText(reason?: string | null): string {
  const detail = reason
    ? EXCLUSION_LABELS[reason] ?? reason.replace(/_/g, ' ')
    : 'reason not recorded';
  return `not included in the calculation — ${detail}`;
}

// Always US format, never the viewer's locale — see src/lib/dates.ts.
const formatDate = (d?: string | null) => formatUsDate(d);

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
  // Source codes are Y/N for EL and ethnicity, and T/F for special education and 504.
  if (v === 'false' || v === '0' || v === 'no' || v === 'n' || v === 'f') return 'No';
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
  const [overrideNoteEla, setOverrideNoteEla] = useState('');
  const [overrideNoteMath, setOverrideNoteMath] = useState('');
  const [isSavingTier, setIsSavingTier] = useState<'ela' | 'math' | null>(null);
  const [isGeneratingAI, setIsGeneratingAI] = useState(false);

  const [showPlanModal, setShowPlanModal] = useState(false);
  const [newPlan, setNewPlan] = useState({ tier: 'Tier 1', strategy: '', customDetails: '', frequency: 'Weekly' });

  const [showDemographics, setShowDemographics] = useState(false);
  const [selectedAssessment, setSelectedAssessment] = useState<Assessment | null>(null);

  // Editing the student's own fields. Only fields the user actually changed are sent: the API
  // applies whatever it receives, so posting the whole form would rewrite — and in the audit
  // trail, report a change to — every column on the record.
  const [editForm, setEditForm] = useState<Record<string, string> | null>(null);
  const [isSavingStudent, setIsSavingStudent] = useState(false);

  // Add/edit one assessment record. Every save re-runs the tier engine server-side and returns the
  // updated student, so the tier on screen always reflects the records beneath it.
  const [recordModal, setRecordModal] = useState<{ id: string | null; value: AssessmentInput } | null>(null);
  const [isSavingRecord, setIsSavingRecord] = useState(false);
  const [deletingRecordId, setDeletingRecordId] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<Assessment | null>(null);
  const [recordError, setRecordError] = useState('');

  // Newest assessment first by default (BRD: "Sorting by Date descending shows the most recent
  // assessment at the top"). Without this the table renders in whatever order the API returned.
  const [assessmentSortConfig, setAssessmentSortConfig] = useState<{ key: string; direction: 'asc' | 'desc' } | null>(
    { key: 'formattedDate', direction: 'desc' }
  );

  // G1 – Audit Trail
  const [auditEntries, setAuditEntries] = useState<AuditEntry[]>([]);
  const [auditLoading, setAuditLoading] = useState(false);
  // Entries that arrived from the most recent action, highlighted briefly so a change is visible
  // without reading timestamps.
  const [newAuditIds, setNewAuditIds] = useState<Set<string>>(new Set());
  // Mirrors auditEntries for refreshAudit: it runs inside handlers that captured an older render,
  // and diffing against that stale list would mark every entry as new.
  const auditEntriesRef = useRef<AuditEntry[]>([]);
  auditEntriesRef.current = auditEntries;
  // Opening a profile writes a "Profile Viewed" entry, so on a student anyone has looked at more
  // than once the views outnumber the changes and bury them. Hidden by default, one click away.
  const [showAuditViews, setShowAuditViews] = useState(false);

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

  useEffect(() => {
    if (newAuditIds.size === 0) return;
    const timer = setTimeout(() => setNewAuditIds(new Set()), 6000);
    return () => clearTimeout(timer);
  }, [newAuditIds]);

  // BRD ST-16 – Generate Recommendation
  const [isGeneratingRec, setIsGeneratingRec] = useState(false);
  const [recMessage, setRecMessage] = useState('');

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
      await refreshAudit();
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
      await refreshAudit();
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
    const note = (subject === 'ela' ? overrideNoteEla : overrideNoteMath).trim();
    if (!value || !student) return;
    if (!note) {
      alert('An explanation is required before an override can be saved.');
      return;
    }
    setIsSavingTier(subject);
    try {
      const updated = await studentsApi.setSubjectTier(studentId, subject, { tier: value, status: 'Admin Override', note });
      setStudent(updated);
      if (subject === 'ela') { setOverrideTierEla(''); setOverrideNoteEla(''); }
      else { setOverrideTierMath(''); setOverrideNoteMath(''); }
      await refreshAudit();
    } catch (e: any) {
      alert('Failed to save tier: ' + e.message);
    } finally {
      setIsSavingTier(null);
    }
  }

  async function handleGenerateRecommendation() {
    const replacingOverride = isAdminOverride(student?.elaTier?.status) || isAdminOverride(student?.mathTier?.status);
    if (replacingOverride && !confirm(
      'Generate Recommendation replaces any administrator override with the tier calculated from this student\'s assessments. Continue?'
    )) return;
    setIsGeneratingRec(true);
    setRecMessage('');
    try {
      const updated = await studentsApi.recalculateTier(studentId);
      setStudent(updated);
      setRecMessage('Recommendation updated from the system tiering rules.');
      await refreshAudit();
    } catch (e: any) {
      alert(e.message || 'Tier calculation failed.');
    } finally {
      setIsGeneratingRec(false);
    }
  }

  function openEditStudent() {
    if (student) setEditForm(openEditValues(student));
  }

  async function handleSaveStudent() {
    if (!editForm || !student) return;
    if (!editForm.fullName.trim()) { alert('Full name cannot be blank.'); return; }

    const original = openEditValues(student);
    const changes: Record<string, string | boolean> = Object.fromEntries(
      Object.entries(editForm).filter(([key, value]) => value !== original[key])
    );
    if (Object.keys(changes).length === 0) { setEditForm(null); return; }
    if (typeof changes.enrollment === 'string') {
      changes.isActive = changes.enrollment === 'Enrolled';
      delete changes.enrollment;
    }

    setIsSavingStudent(true);
    try {
      const updated = await studentsApi.update(studentId, changes as any);
      setStudent(updated);
      setEditForm(null);
      await refreshAudit();
    } catch (e: any) {
      alert('Failed to save: ' + e.message);
    } finally {
      setIsSavingStudent(false);
    }
  }

  async function handleSaveRecord() {
    if (!recordModal) return;
    setIsSavingRecord(true);
    setRecordError('');
    try {
      const value = {
        ...recordModal.value,
        proficiency: matchProficiencyOption(recordModal.value.uploadType, recordModal.value.proficiency, tierRuleset)
          ?? recordModal.value.proficiency,
      };
      const result = recordModal.id
        ? await assessmentsApi.update(studentId, recordModal.id, value)
        : await assessmentsApi.create(studentId, value);
      setStudent(result.student);
      setRecordModal(null);
      await refreshRecordsAndAudit();
    } catch (e: any) {
      setRecordError(e.message || 'Could not save the record.');
    } finally {
      setIsSavingRecord(false);
    }
  }

  async function handleDeleteRecord(assessment: Assessment) {
    setPendingDelete(null);
    setDeletingRecordId(assessment.id);
    try {
      const result = await assessmentsApi.remove(studentId, assessment.id);
      setStudent(result.student);
      await refreshRecordsAndAudit();
    } catch (e: any) {
      alert('Failed to delete the record: ' + e.message);
    } finally {
      setDeletingRecordId(null);
    }
  }

  // Every write on this page lands in the audit trail, so every handler ends by calling this —
  // previously each one re-fetched inline (or, for tier overrides, AI summaries and notes, not at
  // all), so the trail silently disagreed with the page above it until a reload.
  //
  // The re-fetch retries briefly. Audit logs are partitioned by admin email, so reading one back
  // by student is a cross-partition query and does not reliably see a write that completed moments
  // earlier: a single immediate fetch returned the pre-write list, and the entry only surfaced on
  // whatever the user did next. That looked exactly like "the trail doesn't refresh".
  async function refreshAudit(expectChange = true) {
    const before = new Set(auditEntriesRef.current.map(e => e.id));
    const delays = [0, 400, 800, 1200];

    for (let attempt = 0; attempt < delays.length; attempt++) {
      if (delays[attempt]) await new Promise(r => setTimeout(r, delays[attempt]));

      const result = await studentAuditApi.list(studentId)
        .catch(() => ({ items: [] as AuditEntry[], total: 0, page: 1, pageSize: 50 }));
      const arrived = result.items.filter(e => !before.has(e.id));

      // Keep waiting only while a change is expected and none has landed; on the last attempt
      // take whatever the server has rather than leaving the panel stale.
      if (!expectChange || arrived.length > 0 || attempt === delays.length - 1) {
        setAuditEntries(result.items);
        // Anything that wasn't in the list before the action is what the user just did — flagged
        // so the panel points at it instead of leaving them to scan identical-looking rows.
        if (arrived.length) setNewAuditIds(new Set(arrived.map(e => e.id)));
        return;
      }
    }
  }

  // The record list changes on every record write too; the tier comes back on the write response
  // itself, so it is never re-fetched here.
  async function refreshRecordsAndAudit() {
    const records = await assessmentsApi.byStudent(studentId).catch(() => assessments);
    setAssessments(records);
    await refreshAudit();
  }

  async function handleGenerateAI() {
    setIsGeneratingAI(true);
    try {
      const summary = await aiApi.generate(studentId);
      setAiSummary(summary);
      await refreshAudit();
    } catch (e: any) {
      alert(e.message || 'AI summary generation failed.');
    } finally {
      setIsGeneratingAI(false);
    }
  }

  const sortedAssessments = useMemo(() => {
    const rows = assessments.map(a => ({ ...a, displayData: getAssessmentDisplayData(a, tierRuleset) }));
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
  }, [assessments, assessmentSortConfig, tierRuleset]);

  // "Profile Viewed" is written on every page load, including this one, so it is separated from
  // the entries that record an actual change rather than listed alongside them.
  const changeEntries = useMemo(
    () => auditEntries.filter(e => e.eventType !== 'View'), [auditEntries]);
  const viewCount = auditEntries.length - changeEntries.length;
  const changeCount = changeEntries.length;
  const lastChangeAt = changeEntries[0] ? formatAuditTimestamp(changeEntries[0].timestamp) : null;
  const visibleAuditEntries = showAuditViews ? auditEntries : changeEntries;

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

  function tierBadgeColor(tier: string | null | undefined, status: string | null | undefined) {
    // Admin Override renders solid/saturated; System Recommended (and Pending) render outlined —
    // provenance reads from the pill's fill, not just the caption underneath it.
    const override = isAdminOverride(status);
    if (override) {
      return tier === 'Tier 1' ? 'bg-green-600 text-white border-green-600' :
        tier === 'Tier 2' ? 'bg-yellow-600 text-white border-yellow-600' :
        tier === 'Tier 3' ? 'bg-red-600 text-white border-red-600' :
        'bg-slate-500 text-white border-slate-500';
    }
    return tier === 'Tier 1' ? 'bg-white text-green-700 border-green-400' :
      tier === 'Tier 2' ? 'bg-white text-yellow-700 border-yellow-400' :
      tier === 'Tier 3' ? 'bg-white text-red-700 border-red-400' :
      'bg-slate-100 text-slate-600 border-slate-300';
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
        <div className="px-4 pt-5 pb-4 sm:px-6 sm:pt-6 flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">
          <div className="flex items-center gap-4 min-w-0">
            {/* Avatar circle */}
            <div className="w-14 h-14 rounded-full bg-lgs-blue flex items-center justify-center shrink-0 shadow-sm">
              <span className="text-white text-xl font-bold select-none">
                {student.fullName?.trim().split(/\s+/).map(p => p[0]).slice(0, 2).join('').toUpperCase()}
              </span>
            </div>
            <div className="min-w-0">
              <h1 className="text-xl sm:text-2xl font-bold text-lgs-blue leading-tight break-words">
                {formatDisplayName(student.fullName)}
              </h1>
              <div className="flex flex-wrap items-center gap-x-2 gap-y-1 mt-1">
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

          {/* Two independent subject tier badges — no combined overall tier (TR-011).
              Equal columns, centered, so the label, pill, and caption share one axis
              and the caption wraps instead of running out of the card. */}
          <div className="grid grid-cols-2 gap-3 sm:gap-6 w-full lg:w-auto">
            {([['ELA', student.elaTier], ['Math', student.mathTier]] as const).map(([label, t]) => (
              <div key={label} className="flex flex-col items-center text-center min-w-0">
                <p className="text-xs font-bold text-slate-400 uppercase tracking-wide mb-1">{label}</p>
                <span className={`inline-flex items-center justify-center gap-1.5 px-4 py-1.5 rounded-full text-sm font-semibold border-2 whitespace-nowrap ${tierBadgeColor(t?.tier, t?.status)}`}>
                  {isAdminOverride(t?.status) && <Pencil className="w-3.5 h-3.5 shrink-0" />}
                  {t?.tier || 'Pending'}
                </span>
                {t?.status && t.status !== 'Pending' && (
                  <p className="text-xs text-slate-400 mt-1.5 leading-snug max-w-[11rem]">
                    {isAdminOverride(t.status) ? 'Admin Override' : t.status}
                    {t.score != null && (
                      <>
                        <br />
                        score {t.score.toFixed(2)} · {t.dataPoints} assessment{t.dataPoints === 1 ? '' : 's'}
                      </>
                    )}
                  </p>
                )}
                {t?.status === 'Pending' && (
                  <p className="text-xs text-amber-600 mt-1.5 leading-snug max-w-[11rem]">{pendingReasonText(t.pendingReason)}</p>
                )}
              </div>
            ))}
          </div>
        </div>

        {/* Divider */}
        <div className="border-t border-slate-100 mx-4 sm:mx-6" />

        {/* Demographic grid */}
        <div className="px-4 sm:px-6 py-4 grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-x-4 sm:gap-x-6 gap-y-4 text-sm">
          {[
            { label: 'Date of Birth', value: formatUsDate(student.dob) },
            { label: 'Age', value: String(calculateAge(student.dob)) },
            { label: 'Gender', value: student.gender || 'N/A' },
            { label: 'Ethnicity', value: student.ethnicity === 'Y' || student.ethnicity === 'N' ? student.ethnicity : translateEthnicity(student.ethnicity) },
            { label: 'Race', value: optionLabel(RACE_OPTIONS, student.race) || 'N/A' },
            { label: 'EL Status', value: toYesNo(student.ellStatus) },
            { label: 'Sp. Education', value: toYesNo(student.spedStatus) },
            { label: '504 Plan', value: toYesNo(student.section504, 'No') },
            { label: 'Lunch', value: optionLabel(LUNCH_OPTIONS, student.lunchStatus) || 'N/A' },
          ].map(({ label, value }) => (
            <div key={label}>
              <p className="text-xs font-medium text-slate-400 uppercase tracking-wide mb-0.5">{label}</p>
              <p className="text-slate-800 font-medium truncate" title={value}>{value}</p>
            </div>
          ))}
        </div>

        {/* Footer: source line + entry/exit + demographics link (BRD §8.3.2) */}
        <div className="px-4 sm:px-6 pb-4 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-slate-400">
          {student.sourceFile && (
            <span>Source: <span className="text-slate-600 font-mono">{student.sourceFile}</span></span>
          )}
          {student.entryDate && (
            <span>Entry: <span className="text-slate-600">{formatUsDate(student.entryDate)}</span></span>
          )}
          {student.exitDate && (
            <span>Exit: <span className="text-slate-600">{formatUsDate(student.exitDate)}</span></span>
          )}
          <button
            onClick={openEditStudent}
            className="ml-auto flex items-center gap-1 text-slate-500 hover:text-lgs-blue font-medium text-xs"
          >
            <Pencil className="w-3 h-3" />
            Edit Details
          </button>
          <button
            onClick={() => setShowDemographics(true)}
            className="text-lgs-red hover:underline font-medium text-xs"
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
            <div className="flex items-center justify-between mb-4 gap-4">
              <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2">
                <BookOpen className="w-5 h-5 text-lgs-red" />
                Academic Assessments
              </h2>
              <button
                onClick={() => { setRecordError(''); setRecordModal({ id: null, value: { ...EMPTY_RECORD } }); }}
                className="shrink-0 flex items-center gap-1 px-3 py-1.5 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark transition-colors"
                title="Enter an assessment result by hand and recalculate the tier"
              >
                <Plus className="w-4 h-4" />
                Add Record
              </button>
            </div>
            {assessments.length === 0 ? (
              <p className="text-slate-500 text-sm">
                No assessment data yet. Add a record to have the tier engine score this student.
              </p>
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
                      <th className="px-2 py-3 text-right">Actions</th>
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
                          {/* No period means the engine can't assign an evidence weight, so the
                              row is excluded from the tier score entirely. Say so here rather than
                              leaving a blank cell that reads as merely cosmetic. */}
                          <td className="px-4 py-3">
                            {d.period ? d.period : (
                              <span
                                title="This assessment's period (checkpoint or benchmark window) could not be identified, so it carries no evidence weight and is not included in the tier calculation."
                                className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full bg-amber-50 text-amber-700 border border-amber-200 text-xs font-medium whitespace-nowrap"
                              >
                                <AlertTriangle className="w-3 h-3 shrink-0" />
                                No period — not counted
                              </span>
                            )}
                          </td>
                          <td className="px-4 py-3 font-medium">{d.score}</td>
                          <td className="px-4 py-3">
                            <span className={`px-2 py-1 rounded-full text-xs font-medium ${
                              d.proficiency.includes('Below') ? 'bg-red-100 text-red-700' :
                              d.proficiency.includes('Approaching') ? 'bg-yellow-100 text-yellow-700' :
                              d.proficiency.includes('At') || d.proficiency.includes('Above') ? 'bg-green-100 text-green-700' :
                              'bg-slate-100 text-slate-700'
                            }`}>{d.proficiency}</span>
                          </td>
                          {/* Icon-only: this table sits in a two-thirds column and the proficiency
                              badges already wrap at that width, so a text label here pushed the
                              last action out of view entirely. */}
                          <td className="px-2 py-3">
                            <div className="flex items-center justify-end gap-2 whitespace-nowrap">
                              <button
                                onClick={() => setSelectedAssessment(a)}
                                className="text-slate-400 hover:text-lgs-blue"
                                title="View full details for this record"
                              >
                                <Info className="w-4 h-4" />
                              </button>
                              <button
                                onClick={() => { setRecordError(''); setRecordModal({ id: a.id, value: toRecordInput(a) }); }}
                                className="text-slate-400 hover:text-lgs-blue"
                                title="Edit this record and recalculate the tier"
                              >
                                <Pencil className="w-4 h-4" />
                              </button>
                              <button
                                onClick={() => setPendingDelete(a)}
                                disabled={deletingRecordId === a.id}
                                className="text-slate-400 hover:text-red-500 disabled:opacity-40"
                                title="Delete this record and recalculate the tier"
                              >
                                <Trash2 className="w-4 h-4" />
                              </button>
                            </div>
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
                {(() => {
                  const firstName = student?.fullName?.trim().split(/\s+/)[0] ?? 'The student';
                  const displayText = (aiSummary.summaryText || '')
                    // Strip the redundant top-level heading the LLM emits from the prompt template
                    .replace(/^##\s+AI Assistant Summary\s*\n?/im, '')
                    .replace(/\bStudent\s+S-[A-Za-z0-9-]+/gi, firstName)
                    .replace(/\bS-[A-Za-z0-9-]+\b/gi, firstName)
                    .trim();
                  if (!displayText) {
                    return (
                      <p className="text-sm text-slate-500">
                        Generation finished but the model returned no text. Click Regenerate.
                      </p>
                    );
                  }
                  return (
                    <div className="text-sm text-slate-800 leading-relaxed prose prose-sm max-w-none
                      prose-headings:text-slate-800 prose-headings:font-semibold
                      prose-h2:text-base prose-h2:mt-2 prose-h2:mb-1
                      prose-h3:text-sm prose-h3:mt-3 prose-h3:mb-1
                      prose-ul:my-1 prose-li:my-0.5
                      prose-p:my-1">
                      <ReactMarkdown>{displayText}</ReactMarkdown>
                    </div>
                  );
                })()}
                <p className="text-xs text-slate-400 mt-3">Generated: {formatUsDateTime(aiSummary.generatedAt)}</p>
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

            {/* BRD ST-16: one Generate Recommendation button recomputes both subjects from the
                system tiering rules, including a subject an admin has overridden. Override is
                per-subject below since ELA and Math are independent (TR-011). */}
            <div className="mb-4">
              <button
                onClick={handleGenerateRecommendation}
                disabled={isGeneratingRec}
                className="w-full flex items-center justify-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
                title="Recalculate ELA and Math from the system tiering rules"
              >
                <Sparkles className="w-4 h-4" />
                {isGeneratingRec ? 'Calculating…' : 'Generate Recommendation'}
              </button>
              {recMessage && <p className="text-xs text-green-700 mt-2">{recMessage}</p>}
            </div>

            {([
              ['ela', 'ELA', student?.elaTier, overrideTierEla, setOverrideTierEla, overrideNoteEla, setOverrideNoteEla] as const,
              ['math', 'Math', student?.mathTier, overrideTierMath, setOverrideTierMath, overrideNoteMath, setOverrideNoteMath] as const,
            ]).map(([subject, label, t, value, setValue, note, setNote]) => (
              <div key={subject} className="border-t border-slate-100 pt-4 mt-4 first:mt-0 first:border-t-0 first:pt-0">
                <label className="block text-sm font-medium text-slate-700 mb-2">
                  {label} — Admin Override
                  {isAdminOverride(t?.status) && <span className="ml-2 text-xs font-normal text-slate-400">(set by an administrator)</span>}
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
                    disabled={!value || !note.trim() || isSavingTier === subject}
                    className="px-4 py-2 bg-lgs-red text-white text-sm font-medium rounded-lg hover:bg-lgs-red-dark disabled:opacity-50"
                  >
                    {isSavingTier === subject ? '...' : 'Save'}
                  </button>
                </div>
                <label className="block text-xs font-medium text-slate-500 mt-2 mb-1">Explanation (required)</label>
                <textarea
                  value={note}
                  onChange={e => setNote(e.target.value)}
                  rows={2}
                  placeholder="Why is this tier being overridden?"
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none resize-y"
                />
                {t?.overrideExplanation && (
                  <p className="text-xs text-slate-600 mt-2 leading-relaxed">
                    <span className="font-medium text-slate-500">Saved explanation: </span>
                    {t.overrideExplanation}
                  </p>
                )}
                {t?.reasoning && (
                  <details className="mt-2">
                    <summary className="text-xs text-slate-400 cursor-pointer hover:text-lgs-blue">Tiering Evidence</summary>
                    <p className="text-xs text-slate-500 mt-1.5 leading-relaxed">{t.reasoning}</p>
                    {t.evidence.length > 0 && (
                      <ul className="mt-1.5 space-y-0.5">
                        {t.evidence.map(ev => (
                          <li key={ev.assessmentId} className="text-xs">
                            {/* Only the assessment itself is struck through — the reason it was left
                                out has to stay readable, which it isn't under a line-through. */}
                            <span className={ev.counted ? 'text-slate-600' : 'text-slate-400 line-through'}>
                              {ev.source} {ev.period ?? 'period not identified'} "{ev.category ?? 'n/a'}"
                            </span>
                            {ev.counted ? (
                              <span className="text-slate-600"> ({ev.value}×{ev.weight})</span>
                            ) : (
                              <span className="text-amber-700"> — {exclusionText(ev.exclusionReason)}</span>
                            )}
                          </li>
                        ))}
                      </ul>
                    )}
                  </details>
                )}
              </div>
            ))}
          </div>

          {/* G1 – Audit Trail (BRD ST-21) */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <div className="flex items-start justify-between gap-3 mb-1">
              <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2">
                <Clock className="w-5 h-5 text-lgs-red" />
                Audit Trail
              </h2>
              {viewCount > 0 && (
                <button
                  onClick={() => setShowAuditViews(v => !v)}
                  className="shrink-0 text-xs text-slate-400 hover:text-lgs-blue underline decoration-dotted"
                >
                  {showAuditViews ? 'Hide' : 'Show'} {viewCount} view{viewCount === 1 ? '' : 's'}
                </button>
              )}
            </div>
            <p className="text-xs text-slate-400 mb-3">
              {changeCount === 0
                ? 'No changes recorded yet.'
                : `${changeCount} change${changeCount === 1 ? '' : 's'}${lastChangeAt ? ` · latest ${lastChangeAt}` : ''}`}
            </p>
            {visibleAuditEntries.length === 0 ? (
              <p className="text-slate-500 text-sm">No audit events recorded yet.</p>
            ) : (
              <ul className="space-y-2 max-h-80 overflow-y-auto pr-1">
                {visibleAuditEntries.map(e => {
                  const isNew = newAuditIds.has(e.id);
                  return (
                    <li
                      key={e.id}
                      className={`text-xs border-b border-slate-100 pb-2 last:border-0 last:pb-0 ${
                        isNew ? 'bg-amber-50 -mx-2 px-2 py-1.5 rounded-lg border-b-0' : ''
                      }`}
                    >
                      <div className="flex items-center gap-2 flex-wrap">
                        <span className="font-semibold text-slate-800">
                          {AUDIT_EVENT_LABELS[e.eventType] ?? e.eventType}
                        </span>
                        {isNew && (
                          <span className="px-1.5 py-0.5 rounded-full bg-amber-200 text-amber-900 text-[10px] font-semibold uppercase tracking-wide">
                            Just now
                          </span>
                        )}
                        <span className="text-slate-400">{formatAuditTimestamp(e.timestamp)}</span>
                      </div>
                      <p className="text-slate-500 mt-0.5">{e.adminEmail}</p>
                      {e.details && <p className="text-slate-600 mt-0.5 leading-snug">{e.details}</p>}
                    </li>
                  );
                })}
              </ul>
            )}
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

      {pendingDelete && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-md w-full p-6">
            <h3 className="text-lg font-bold text-slate-900">Delete Assessment Record</h3>
            <p className="text-sm text-slate-600 mt-2">
              Are you sure you want to delete this assessment record?
            </p>
            <div className="mt-6 flex justify-end gap-3">
              <button
                type="button"
                onClick={() => setPendingDelete(null)}
                className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => handleDeleteRecord(pendingDelete)}
                disabled={deletingRecordId === pendingDelete.id}
                className="px-4 py-2 bg-lgs-red text-white font-medium hover:bg-lgs-red-dark rounded-lg disabled:opacity-50"
              >
                {deletingRecordId === pendingDelete.id ? 'Deleting…' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Add / edit one assessment record — the tier is recalculated on save */}
      {recordModal && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-2xl w-full p-6 max-h-[90vh] overflow-y-auto">
            <div className="flex justify-between items-center mb-1">
              <h3 className="text-lg font-bold text-slate-900">
                {recordModal.id ? 'Edit Assessment Record' : 'Add Assessment Record'}
              </h3>
              <button onClick={() => setRecordModal(null)} className="text-slate-400 hover:text-slate-600">
                <X className="w-5 h-5" />
              </button>
            </div>
            <p className="text-sm text-slate-500 mb-4">
              Saved records are normalized and scored exactly as imported ones are. The tier is
              recalculated as soon as you save — except for a subject set by Admin Override, which is
              left as it is.
            </p>

            <AssessmentRecordFields
              value={recordModal.value}
              ruleset={tierRuleset}
              onChange={next => setRecordModal(prev => (prev ? { ...prev, value: next } : prev))}
            />

            {recordError && <p className="text-sm text-red-600 mt-3">{recordError}</p>}

            <div className="mt-6 flex justify-end gap-3">
              <button onClick={() => setRecordModal(null)} className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors">
                Cancel
              </button>
              <button
                onClick={handleSaveRecord}
                disabled={isSavingRecord}
                className="px-4 py-2 bg-lgs-red text-white font-medium hover:bg-lgs-red-dark rounded-lg disabled:opacity-50"
              >
                {isSavingRecord ? 'Saving…' : 'Save & Recalculate'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Edit the student's own fields */}
      {editForm && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-2xl w-full p-6 max-h-[90vh] overflow-y-auto">
            <div className="flex justify-between items-center mb-4">
              <h3 className="text-lg font-bold text-slate-900">Edit Student Details</h3>
              <button onClick={() => setEditForm(null)} className="text-slate-400 hover:text-slate-600">
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
              {([
                ['fullName', 'Full Name', 'text'],
                ['stn', 'STN', 'text'],
                ['dob', 'Date of Birth', 'date'],
                ['grade', 'Grade', 'text'],
                ['classGroup', 'Class Group', 'text'],
                ['homeRoom', 'Homeroom', 'text'],
                ['entryDate', 'Entry Date', 'date'],
                ['exitDate', 'Exit Date', 'date'],
              ] as const).map(([key, label, type]) => (
                <div key={key}>
                  <label className="block text-xs font-medium text-slate-500 mb-1">{label}</label>
                  <input
                    type={type}
                    value={editForm[key] ?? ''}
                    onChange={e => setEditForm(prev => (prev ? { ...prev, [key]: e.target.value } : prev))}
                    className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none"
                  />
                </div>
              ))}
              {([
                ['gender', 'Gender', GENDER_OPTIONS],
                ['ethnicity', 'Ethnicity', ETHNICITY_OPTIONS],
                ['race', 'Race', RACE_OPTIONS],
                ['enrollment', 'Enrollment Status', ENROLLMENT_OPTIONS],
                ['lunchStatus', 'Lunch Status', LUNCH_OPTIONS],
                ['ellStatus', 'EL / ELL Status', ELL_OPTIONS],
                ['spedStatus', 'Special Education', TRUE_FALSE_OPTIONS],
                ['section504', '504 Status', TRUE_FALSE_OPTIONS],
              ] as const).map(([key, label, options]) => (
                <div key={key}>
                  <label className="block text-xs font-medium text-slate-500 mb-1">{label}</label>
                  <select
                    value={editForm[key] ?? ''}
                    onChange={e => setEditForm(prev => (prev ? { ...prev, [key]: e.target.value } : prev))}
                    className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none bg-white"
                  >
                    {optionsWithCurrent(options, editForm[key]).map(o => (
                      <option key={o.value || 'blank'} value={o.value}>{o.label}</option>
                    ))}
                  </select>
                </div>
              ))}
            </div>

            <p className="text-xs text-slate-400 mt-4">
              Demographics only. Tiers are changed with Generate Recommendation or the per-subject
              Admin Override, and every edit here is recorded in the audit trail.
            </p>

            <div className="mt-6 flex justify-end gap-3">
              <button onClick={() => setEditForm(null)} className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors">
                Cancel
              </button>
              <button
                onClick={handleSaveStudent}
                disabled={isSavingStudent}
                className="px-4 py-2 bg-lgs-red text-white font-medium hover:bg-lgs-red-dark rounded-lg disabled:opacity-50"
              >
                {isSavingStudent ? 'Saving…' : 'Save Changes'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Demographics Modal */}
      {showDemographics && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-3xl w-full">
            <div className="flex items-center justify-between px-6 py-4 border-b border-slate-100">
              <h3 className="text-lg font-bold text-slate-900">Full Demographics</h3>
              <button onClick={() => setShowDemographics(false)} className="text-slate-400 hover:text-slate-600" title="Close">
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="px-6 py-4 space-y-4">
              <section>
                <p className="text-[11px] font-semibold text-slate-400 uppercase tracking-wide mb-2">Student Identity</p>
                <div className="grid grid-cols-4 gap-x-6 gap-y-3">
                  {[
                    ['Full Name', formatDisplayName(student.fullName)],
                    ['STN', student.stn || 'N/A'],
                    ['Date of Birth', formatUsDate(student.dob)],
                    ['Age', String(calculateAge(student.dob))],
                    ['Gender', student.gender || 'N/A'],
                    ['Ethnicity', student.ethnicity === 'Y' || student.ethnicity === 'N' ? student.ethnicity : translateEthnicity(student.ethnicity)],
                    ['Race', optionLabel(RACE_OPTIONS, student.race) || 'N/A'],
                  ].map(([label, value]) => (
                    <div key={label} className="min-w-0">
                      <span className="block text-[11px] text-slate-400">{label}</span>
                      <span className="block text-sm font-medium text-slate-900 truncate" title={value}>{value}</span>
                    </div>
                  ))}
                </div>
              </section>

              <section className="border-t border-slate-100 pt-4">
                <p className="text-[11px] font-semibold text-slate-400 uppercase tracking-wide mb-2">Enrollment</p>
                <div className="grid grid-cols-4 gap-x-6 gap-y-3">
                  {[
                    ['Grade', normalizeGradeLabel(student.grade) || 'N/A'],
                    ['Class Group', student.classGroup || 'N/A'],
                    ['Homeroom', student.homeRoom || 'N/A'],
                    ['Enrollment Status', student.isActive === false ? 'Unenrolled' : 'Enrolled'],
                    ['Entry Date', formatUsDate(student.entryDate)],
                    ['Exit Date', formatUsDate(student.exitDate)],
                    ['Enrolled', formatUsDate(student.enrolDate)],
                  ].map(([label, value]) => (
                    <div key={label} className="min-w-0">
                      <span className="block text-[11px] text-slate-400">{label}</span>
                      <span className="block text-sm font-medium text-slate-900 truncate" title={value}>{value}</span>
                    </div>
                  ))}
                </div>
              </section>

              <section className="border-t border-slate-100 pt-4">
                <p className="text-[11px] font-semibold text-slate-400 uppercase tracking-wide mb-2">Programs & Language</p>
                <div className="grid grid-cols-4 gap-x-6 gap-y-3">
                  {[
                    ['Special Education', toYesNo(student.spedStatus)],
                    ['504 Plan', toYesNo(student.section504, 'No')],
                    ['Lunch Status', optionLabel(LUNCH_OPTIONS, student.lunchStatus) || 'N/A'],
                    ['EL Status', toYesNo(student.ellStatus)],
                  ].map(([label, value]) => (
                    <div key={label} className="min-w-0">
                      <span className="block text-[11px] text-slate-400">{label}</span>
                      <span className="block text-sm font-medium text-slate-900 truncate" title={value}>{value}</span>
                    </div>
                  ))}
                </div>
              </section>

              <section className="border-t border-slate-100 pt-4">
                <p className="text-[11px] font-semibold text-slate-400 uppercase tracking-wide mb-2">Source</p>
                <span className="block text-[11px] text-slate-400">Source File</span>
                <span className="block text-xs font-medium text-slate-900 font-mono break-all">{student.sourceFile || 'Unknown'}</span>
              </section>
            </div>

            <div className="px-6 py-3 border-t border-slate-100 flex justify-end">
              <button onClick={() => setShowDemographics(false)} className="px-4 py-2 bg-slate-100 text-slate-700 text-sm font-medium hover:bg-slate-200 rounded-lg transition-colors">
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
                  ['Proficiency', matchProficiencyOption(selectedAssessment.uploadType, selectedAssessment.proficiency, tierRuleset)
                    ?? normalizeProficiency(selectedAssessment.proficiency ?? 'N/A')],
                  ['Period', selectedAssessment.period ?? 'Not identified — not counted'],
                  ['Date', formatDate(selectedAssessment.date ?? '')],
                ].map(([label, value]) => (
                  <div key={String(label)}>
                    <span className="block text-xs text-slate-500 font-medium mb-0.5">{label}</span>
                    <span className="text-slate-900 font-medium">{String(value)}</span>
                  </div>
                ))}
              </div>

              {/* Provenance — which file this row came from, and when it was ingested.
                  Needed to tell a duplicate re-import apart from two genuinely different source files. */}
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 bg-slate-50 p-4 rounded-lg border border-slate-100 text-sm mb-4">
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-0.5">Source File</span>
                  <span className="text-slate-900 font-mono text-xs break-all">{selectedAssessment.fileName || 'Unknown'}</span>
                </div>
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-0.5">Imported At</span>
                  <span className="text-slate-900 font-medium">
                    {selectedAssessment.uploadedAt ? formatDate(selectedAssessment.uploadedAt) : 'Unknown'}
                  </span>
                </div>
                {selectedAssessment.periodRaw && (
                  <div>
                    <span className="block text-xs text-slate-500 font-medium mb-0.5">Period (raw)</span>
                    <span className="text-slate-900 font-mono text-xs">{selectedAssessment.periodRaw}</span>
                  </div>
                )}
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-0.5">Record ID</span>
                  <span className="text-slate-900 font-mono text-xs break-all">{selectedAssessment.id}</span>
                </div>
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
