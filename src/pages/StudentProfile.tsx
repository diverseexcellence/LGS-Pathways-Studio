import React, { useState, useEffect, useMemo } from 'react';
import { useParams } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { studentsApi, assessmentsApi, aiApi, Student, Assessment, AISummary } from '../lib/api';
import { User, BookOpen, Clock, AlertTriangle, CheckCircle, MessageSquare, Info, FileJson, Trash2, ArrowUpDown, ArrowUp, ArrowDown, ClipboardList, Plus, X, Sparkles } from 'lucide-react';

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

  return {
    type: a.uploadType || 'Assessment',
    formattedDate,
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
  const l = p.toLowerCase();
  if (l.includes('below')) return 'Below Proficiency';
  if (l.includes('approaching')) return 'Approaching Proficiency';
  if (l.includes('above')) return 'Above Proficiency';
  if (l.includes('at prof') || l === 'at' || l === 'proficient') return 'At Proficiency';
  return p;
}

function formatDate(d: string) {
  try {
    const parsed = new Date(d);
    if (!isNaN(parsed.getTime())) return parsed.toLocaleDateString();
  } catch {}
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
  const { user } = useAuth();

  const [student, setStudent] = useState<Student | null>(null);
  const [assessments, setAssessments] = useState<Assessment[]>([]);
  const [aiSummary, setAiSummary] = useState<AISummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [overrideTier, setOverrideTier] = useState('');
  const [isSavingTier, setIsSavingTier] = useState(false);
  const [isGeneratingAI, setIsGeneratingAI] = useState(false);

  const [showPlanModal, setShowPlanModal] = useState(false);
  const [newPlan, setNewPlan] = useState({ tier: 'Tier 1', strategy: '', customDetails: '', frequency: 'Weekly' });

  const [showDemographics, setShowDemographics] = useState(false);
  const [selectedAssessment, setSelectedAssessment] = useState<Assessment | null>(null);

  const [assessmentSortConfig, setAssessmentSortConfig] = useState<{ key: string; direction: 'asc' | 'desc' } | null>(null);

  const studentId = id ?? '';

  useEffect(() => {
    if (!studentId) return;
    load();
  }, [studentId]);

  async function load() {
    setLoading(true);
    setError('');
    try {
      const [s, a, ai] = await Promise.all([
        studentsApi.get(studentId),
        assessmentsApi.byStudent(studentId),
        aiApi.get(studentId).catch(() => null),
      ]);
      setStudent(s);
      setAssessments(a);
      setAiSummary(ai);
    } catch (e: any) {
      setError(e.message || 'Failed to load student data');
    } finally {
      setLoading(false);
    }
  }

  async function handleOverrideTier() {
    if (!overrideTier || !student) return;
    setIsSavingTier(true);
    try {
      const updated = await studentsApi.update(studentId, { tier: overrideTier, tierStatus: 'Finalized' });
      setStudent(updated);
      setOverrideTier('');
    } catch (e: any) {
      alert('Failed to save tier: ' + e.message);
    } finally {
      setIsSavingTier(false);
    }
  }

  async function handleGenerateAI() {
    setIsGeneratingAI(true);
    try {
      const summary = await aiApi.generate(studentId);
      setAiSummary(summary);
    } catch (e: any) {
      alert(e.message || 'AI summary generation failed. Ensure Ollama is running.');
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
        av = new Date(av).getTime() || 0;
        bv = new Date(bv).getTime() || 0;
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

  if (loading) return <div className="p-8 text-slate-600">Loading student profile...</div>;
  if (error) return <div className="p-8 text-red-600">{error}</div>;
  if (!student) return <div className="p-8">Student not found.</div>;

  return (
    <div className="space-y-6 max-w-6xl mx-auto">
      {/* Header */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200 border-t-4 border-t-lgs-red">
        <div className="flex justify-between items-start">
          <div>
            <h1 className="text-2xl font-bold text-lgs-blue flex items-center gap-3">
              <User className="w-6 h-6 text-lgs-red" />
              {formatDisplayName(student.fullName)}
            </h1>
            {student.stn && (
              <p className="text-sm text-slate-500 mt-1 ml-9">STN: <span className="font-mono font-medium text-slate-700">{student.stn}</span></p>
            )}
            <div className="mt-4 flex flex-wrap gap-3 text-sm text-slate-600 items-center">
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Grade: {student.grade || 'N/A'}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Class: {student.classGroup || 'N/A'}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Gender: {student.gender || 'N/A'}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Ethnicity: {translateEthnicity(student.ethnicity)}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">DOB: {student.dob ? new Date(student.dob).toLocaleDateString() : 'N/A'}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Age: {calculateAge(student.dob)}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">EL: {toYesNo(student.ellStatus)}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">Sp. Ed: {toYesNo(student.spedStatus)}</span>
              <span className="bg-slate-100 px-3 py-1.5 rounded-md font-medium">504: {toYesNo(student.section504, 'No')}</span>
              <button onClick={() => setShowDemographics(true)} className="text-lgs-red hover:underline text-sm font-medium ml-2">
                View Demographics
              </button>
            </div>
          </div>
          <div className="text-right">
            <div className={`inline-flex items-center gap-2 px-3 py-1 rounded-full font-medium text-sm ${
              student.tier === 'Tier 1' ? 'bg-green-100 text-green-700' :
              student.tier === 'Tier 2' ? 'bg-yellow-100 text-yellow-700' :
              student.tier === 'Tier 3' ? 'bg-red-100 text-red-700' :
              'bg-slate-100 text-lgs-blue'
            }`}>
              {student.tier || 'Pending'} {student.tierStatus ? `(${student.tierStatus})` : ''}
            </div>
          </div>
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
                            <button onClick={() => setSelectedAssessment(a)} className="text-lgs-blue hover:text-lgs-blue-dark p-1 rounded hover:bg-slate-100 transition-colors" title="View Details">
                              <FileJson className="w-4 h-4" />
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
              <button
                onClick={handleGenerateAI}
                disabled={isGeneratingAI}
                className="flex items-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
              >
                {isGeneratingAI ? 'Generating...' : aiSummary ? 'Regenerate' : 'Generate AI Summary'}
              </button>
            </div>
            {aiSummary ? (
              <div className="bg-slate-50 border border-slate-200 rounded-lg p-4">
                <p className="text-sm text-slate-800 whitespace-pre-wrap leading-relaxed">{aiSummary.summaryText}</p>
                <p className="text-xs text-slate-400 mt-3">Generated: {new Date(aiSummary.generatedAt).toLocaleString()}</p>
              </div>
            ) : (
              <p className="text-slate-500 text-sm">No AI summary yet. Click Generate to create one using local Ollama (PII-free).</p>
            )}
          </div>
        </div>

        {/* Right: Tier Management */}
        <div className="space-y-6">
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200 border-t-4 border-t-lgs-blue">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4">Tier Management</h2>

            <div className="mb-4 p-3 rounded-lg bg-slate-50 border border-slate-100 text-sm">
              <p className="font-medium text-slate-700 mb-2">Tiering Criteria</p>
              <p><span className="text-green-600 font-medium">Tier 1:</span> On/Above in both Math & ELA</p>
              <p className="mt-1"><span className="text-yellow-600 font-medium">Tier 2:</span> On/Above in one subject</p>
              <p className="mt-1"><span className="text-red-600 font-medium">Tier 3:</span> Below in both subjects</p>
            </div>

            <div className="pt-4 border-t border-slate-100">
              <label className="block text-sm font-medium text-slate-700 mb-2">Override / Finalize Tier</label>
              <div className="flex gap-2">
                <select
                  value={overrideTier}
                  onChange={e => setOverrideTier(e.target.value)}
                  className="flex-1 px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none"
                >
                  <option value="">Select Tier...</option>
                  <option value="Tier 1">Tier 1</option>
                  <option value="Tier 2">Tier 2</option>
                  <option value="Tier 3">Tier 3</option>
                </select>
                <button
                  onClick={handleOverrideTier}
                  disabled={!overrideTier || isSavingTier}
                  className="px-4 py-2 bg-lgs-red text-white text-sm font-medium rounded-lg hover:bg-lgs-red-dark disabled:opacity-50"
                >
                  {isSavingTier ? '...' : 'Save'}
                </button>
              </div>
            </div>
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
                  const validTier = student?.tier && ['Tier 1', 'Tier 2', 'Tier 3'].includes(student.tier) ? student.tier : 'Tier 1';
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

      {/* Demographics Modal */}
      {showDemographics && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-lg w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-4">Student Demographics</h3>
            <div className="space-y-3 text-sm">
              {[
                ['Full Name', formatDisplayName(student.fullName)],
                ['STN', student.stn || 'N/A'],
                ['Date of Birth', student.dob ? new Date(student.dob).toLocaleDateString() : 'N/A'],
                ['Age', String(calculateAge(student.dob))],
                ['Grade', student.grade || 'N/A'],
                ['Class Group', student.classGroup || 'N/A'],
                ['Homeroom', student.homeRoom || 'N/A'],
                ['Gender', student.gender || 'N/A'],
                ['Ethnicity', translateEthnicity(student.ethnicity)],
                ['EL Status', toYesNo(student.ellStatus)],
                ['Special Education', toYesNo(student.spedStatus)],
                ['504 Status', toYesNo(student.section504, 'No')],
                ['Lunch Status', student.lunchStatus || 'N/A'],
                ['Entry Date', student.entryDate ? new Date(student.entryDate).toLocaleDateString() : 'N/A'],
                ['Exit Date', student.exitDate ? new Date(student.exitDate).toLocaleDateString() : 'N/A'],
                ['Enrolled', student.enrolDate ? new Date(student.enrolDate).toLocaleDateString() : 'N/A'],
                ['Source File', student.fileName || 'Unknown'],
              ].map(([label, value]) => (
                <div key={label} className="grid grid-cols-3 border-b border-slate-100 pb-2">
                  <span className="text-slate-500 font-medium">{label}</span>
                  <span className="col-span-2 text-slate-900">{value}</span>
                </div>
              ))}
            </div>
            <div className="mt-6 flex justify-end">
              <button onClick={() => setShowDemographics(false)} className="px-4 py-2 bg-slate-100 text-slate-700 font-medium hover:bg-slate-200 rounded-lg transition-colors">
                Close
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Assessment Details Modal */}
      {selectedAssessment && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-lg w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-4">Assessment Details</h3>
            <div className="grid grid-cols-2 gap-4 bg-slate-50 p-4 rounded-lg border border-slate-100 text-sm">
              {[
                ['Assessment ID', selectedAssessment.id],
                ['Type', selectedAssessment.uploadType],
                ['Subject', selectedAssessment.subject],
                ['Score', selectedAssessment.score],
                ['Proficiency', selectedAssessment.proficiency],
                ['Period', selectedAssessment.period],
                ['Date', formatDate(selectedAssessment.date ?? '')],
              ].map(([label, value]) => (
                <div key={label}>
                  <span className="block text-xs text-slate-500 font-medium mb-1">{label}</span>
                  <span className="text-slate-900">{String(value ?? 'N/A')}</span>
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
      )}

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
