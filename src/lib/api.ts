import { getMemoryToken } from '../contexts/AuthContext';

const API_BASE = import.meta.env.VITE_API_URL || 'http://localhost:5000';

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const token = getMemoryToken();
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string>),
  };
  if (token) headers['Authorization'] = `Bearer ${token}`;

  const res = await fetch(`${API_BASE}${path}`, { ...options, headers });

  if (res.status === 401) {
    // Token expired — force reload to login
    sessionStorage.removeItem('lgs_token');
    window.location.href = '/';
    throw new Error('Session expired');
  }

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || `Request failed: ${res.status}`);
  }

  if (res.status === 204) return undefined as T;
  return res.json();
}

// Multipart upload (no Content-Type override — browser sets boundary)
async function upload<T>(path: string, formData: FormData): Promise<T> {
  const token = getMemoryToken();
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;

  const res = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers,
    body: formData,
  });

  if (res.status === 401) {
    sessionStorage.removeItem('lgs_token');
    window.location.href = '/';
    throw new Error('Session expired');
  }

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || `Upload failed: ${res.status}`);
  }

  return res.json();
}

// ─── Types ────────────────────────────────────────────────────────────────────

// Two independent subject-level tiers — there is no combined overall student tier (TR-011).
export interface TierEvidence {
  assessmentId: string;
  source: string;
  period: string | null;
  category: string | null;
  value: number | null;
  weight: number | null;
  date: string | null;
  counted: boolean;
  exclusionReason: string | null;
}

export interface SubjectTier {
  tier: string | null;
  status: string; // "Pending" | "System Recommended" | "Finalized"
  score: number | null;
  dataPoints: number;
  pendingReason: string | null;
  reasoning: string | null;
  rulesetVersion: string | null;
  computedAt: string | null;
  overriddenBy: string | null;
  overriddenAt: string | null;
  evidence: TierEvidence[];
}

export interface Student {
  studentId: string;
  fullName: string;
  dob: string;
  classGroup: string;
  enrolDate: string;
  isActive: boolean;
  elaTier: SubjectTier;
  mathTier: SubjectTier;
  grade?: string;
  gender?: string;
  ethnicity?: string;
  ellStatus?: string;
  spedStatus?: string;
  section504?: string;
  stn?: string;
  homeRoom?: string;
  entryDate?: string;
  exitDate?: string;
  lunchStatus?: string;
  sourceFile?: string;
}

export interface AuditEntry {
  id: string;
  adminId: number;
  adminEmail: string;
  eventType: string;
  entityType: string | null;
  entityId: string | null;
  details: string | null;
  timestamp: string;
  ipAddress: string | null;
}

export interface CollaborationNote {
  id: string;
  studentId: string;
  text: string;
  createdAt: string;
  createdBy: string;
  isDeleted: boolean;
  deletedAt?: string;
  deletedBy?: string;
}

export interface Assessment {
  id: string;
  studentId: string;
  subject: string;
  score: number | null;
  proficiency: string | null;
  period: string | null;
  periodRaw?: string | null;
  uploadType: string;
  date: string | null;
  dateIso?: string | null;
  rawFields?: Record<string, string>;
}

export interface AISummary {
  id: string;
  studentId: string;
  summaryText: string;
  generatedAt: string;
  modelUsed?: string;
}

export interface UploadLog {
  id: string;
  fileName: string;
  uploadType: string;
  uploadedAt: string;
  recordCount: number;
  skippedCount?: number;
  errors?: string[];
  blobUrl?: string;
  uploadedBy?: string;
}

export interface StudentListParams {
  page?: number;
  pageSize?: number;
  search?: string;
  classGroup?: string;
  sortBy?: string;
  sortDir?: 'asc' | 'desc';
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ParseSummary {
  totalRows: number;
  importedRows: number;
  skippedRows: number;
  duplicates: Student[];
  errors: string[];
  duplicateAssessments?: number;
  correctedAssessments?: number;
}

// ─── Auth ─────────────────────────────────────────────────────────────────────

export const authApi = {
  login: (email: string, password: string) =>
    request<{ token: string; adminId: number; email: string; name: string }>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),
};

// ─── Students ─────────────────────────────────────────────────────────────────

export const studentsApi = {
  list: (params: StudentListParams = {}) => {
    const qs = new URLSearchParams(
      Object.entries({ page: '1', pageSize: '50', ...params }).reduce((acc, [k, v]) => {
        if (v !== undefined) acc[k] = String(v);
        return acc;
      }, {} as Record<string, string>)
    ).toString();
    return request<PagedResult<Student>>(`/api/students?${qs}`);
  },

  get: (id: string) => request<Student>(`/api/students/${id}`),

  update: (id: string, data: Partial<Student>) =>
    request<Student>(`/api/students/${id}`, {
      method: 'PATCH',
      body: JSON.stringify(data),
    }),

  softDelete: (id: string) =>
    request<void>(`/api/students/${id}`, { method: 'DELETE' }),

  recalculateTier: (id: string) =>
    request<Student>(`/api/students/${id}/recalculate-tier`, { method: 'POST' }),

  // Per-subject override/finalize — there is no combined tier to set (TR-011).
  setSubjectTier: (id: string, subject: 'ela' | 'math', data: { tier?: string; status?: string; note?: string }) =>
    request<Student>(`/api/students/${id}/tier/${subject}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  backfillStn: () =>
    request<{ stnUpdated: number; dobUpdated: number; unmatched: number }>('/api/students/backfill-stn', {
      method: 'POST',
    }),
};

// ─── Assessments ──────────────────────────────────────────────────────────────

export const assessmentsApi = {
  byStudent: (studentId: string) =>
    request<Assessment[]>(`/api/assessments?studentId=${studentId}`),

  bySubject: (studentId: string, subject: string) =>
    request<Assessment[]>(`/api/assessments?studentId=${studentId}&subject=${subject}`),
};

export interface LandingZoneImportStatus {
  state: 'idle' | 'running' | 'completed' | 'failed';
  startedAt: string | null;
  completedAt: string | null;
  message: string | null;
  results: { file: string; uploadType?: string; result?: ParseSummary; error?: string }[] | null;
  error: string | null;
}

// ─── Upload ───────────────────────────────────────────────────────────────────

export const uploadApi = {
  upload: (file: File, uploadType: string) => {
    const fd = new FormData();
    fd.append('file', file);
    fd.append('uploadType', uploadType);
    return upload<ParseSummary>('/api/upload', fd);
  },

  logs: () => request<UploadLog[]>('/api/upload/logs'),

  deleteLog: (id: string) => request<void>(`/api/upload/logs/${id}`, { method: 'DELETE' }),

  // Starts the import in the background and returns immediately — with a couple dozen landing-zone
  // files and per-row Cosmos lookups, the old synchronous version routinely exceeded Azure App
  // Service's platform request timeout, which reset the connection mid-response (surfaced to the
  // browser as a JSON parse error) even though the import kept running to completion server-side.
  // Poll importLandingZoneStatus() for progress and the final result.
  importLandingZone: (only?: string) =>
    request<{ message: string; status: string }>(
      `/api/upload/import-landing-zone${only ? `?only=${encodeURIComponent(only)}` : ''}`,
      { method: 'POST' },
    ),

  importLandingZoneStatus: () =>
    request<LandingZoneImportStatus>('/api/upload/import-landing-zone/status'),

  recalculateTiers: () =>
    request<{ message: string; processed: number; updated: number }>(
      '/api/upload/recalculate-tiers',
      { method: 'POST' }
    ),

  // Per-student/subject counted vs excluded evidence — the "why is this student Pending?" report,
  // and the tool for verifying a re-upload landed correctly after the clean-cutover purge.
  tierDataQuality: () =>
    request<{ summary: unknown[]; students: unknown[] }>('/api/upload/tier-data-quality'),

  // Irreversible: deletes every student and assessment document. Requires the exact confirmation
  // phrase the backend expects. Used once, for the tier-engine clean cutover.
  purgeAll: (confirm: string) =>
    request<{ studentsDeleted: number; assessmentsDeleted: number }>('/api/upload/purge-all', {
      method: 'POST',
      body: JSON.stringify({ confirm }),
    }),
};

// ─── Export ───────────────────────────────────────────────────────────────────

export const exportApi = {
  download: async () => {
    const token = getMemoryToken();
    const headers: Record<string, string> = {};
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const res = await fetch(`${API_BASE}/api/export`, { headers });
    if (!res.ok) throw new Error('Export failed');

    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `lgs-students-export-${new Date().toISOString().slice(0, 10)}.xlsx`;
    a.click();
    URL.revokeObjectURL(url);
  },

  unmatchedStns: async () => {
    const token = getMemoryToken();
    const headers: Record<string, string> = {};
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const res = await fetch(`${API_BASE}/api/export/unmatched-stns`, { headers });
    if (!res.ok) throw new Error('Failed to generate unmatched STN report');

    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `unmatched-stns-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  },
};

// ─── AI Summaries ─────────────────────────────────────────────────────────────

export const aiApi = {
  get: (studentId: string) =>
    request<AISummary | null>(`/api/ai/summary/${studentId}`),

  generate: (studentId: string) =>
    request<AISummary>(`/api/ai/summary/${studentId}`, { method: 'POST' }),
};

// ─── Audit ────────────────────────────────────────────────────────────────────

export const auditApi = {
  list: () => request<any[]>('/api/audit'),
};

export const studentAuditApi = {
  list: (studentId: string, page = 1, pageSize = 50) =>
    request<PagedResult<AuditEntry>>(`/api/students/${studentId}/audit?page=${page}&pageSize=${pageSize}`),
};

// ─── Collaboration Notes ──────────────────────────────────────────────────────

export const notesApi = {
  list: (studentId: string) =>
    request<CollaborationNote[]>(`/api/students/${studentId}/notes`),

  create: (studentId: string, text: string) =>
    request<CollaborationNote>(`/api/students/${studentId}/notes`, {
      method: 'POST',
      body: JSON.stringify({ text }),
    }),

  delete: (studentId: string, noteId: string) =>
    request<void>(`/api/students/${studentId}/notes/${noteId}`, { method: 'DELETE' }),
};

// ─── Dashboard ────────────────────────────────────────────────────────────────

export type TierSubject = 'ela' | 'math';

export interface GradeRow { grade: string; tier1: number; tier2: number; tier3: number; total: number }
export interface GradeProficiencyRow { grade: string; above: number; on: number; approaching: number; below: number; totalStudents: number }
export interface TeacherRow { teacher: string; tier1: number; tier2: number; tier3: number; total: number }
export interface DrillStudent {
  studentId: string;
  fullName: string;
  elaTier: string | null;
  elaTierStatus: string;
  mathTier: string | null;
  mathTierStatus: string;
  classGroup: string;
  homeRoom: string | null;
}
export interface TimelinePoint { month: string; year: number; monthKey: string; ela: number | null; math: number | null }
export interface TierCounts { tier1: number; tier2: number; tier3: number; pending: number }
export interface DashboardKpis {
  mathProficiencyPct: number | null;
  mathStudentsTotal: number;
  mathStudentsOnAbove: number;
  elaGrowthAvgDelta: number | null;
  elaStudentsWithGrowthData: number;
  elaTierCounts: TierCounts;
  mathTierCounts: TierCounts;
}

export interface GeoZipRow {
  zip: string;
  total: number;
  elaTier1: number; elaTier2: number; elaTier3: number;
  mathTier1: number; mathTier2: number; mathTier3: number;
}
export interface UnmatchedStnRow { stn: string; uploadType: string; fileName: string; uploadedAt: string }

export const dashboardApi = {
  getTargetGoal: () => request<{ goalPct: number; updatedAt: string; updatedBy: string | null }>('/api/dashboard/target-goal'),
  setTargetGoal: (goalPct: number) => request<{ goalPct: number }>('/api/dashboard/target-goal', { method: 'PUT', body: JSON.stringify({ goalPct }) }),
  byGrade: (subject: TierSubject = 'ela') => request<GradeRow[]>(`/api/dashboard/by-grade?subject=${subject}`),
  teachersByGrade: (grade: string, subject: TierSubject = 'ela') =>
    request<TeacherRow[]>(`/api/dashboard/by-grade/${encodeURIComponent(grade)}/teachers?subject=${subject}`),
  studentsByGrade: (grade: string) => request<DrillStudent[]>(`/api/dashboard/by-grade/${encodeURIComponent(grade)}/students`),
  kpis: () => request<DashboardKpis>('/api/dashboard/kpis'),
  timeline: () => request<TimelinePoint[]>('/api/dashboard/timeline'),
  byGradeProficiency: () => request<GradeProficiencyRow[]>('/api/dashboard/by-grade-proficiency'),
  geographic: () => request<GeoZipRow[]>('/api/dashboard/geographic'),
};

// ─── Tier ruleset config ───────────────────────────────────────────────────────

export interface TierThreshold { tier: string; minScoreInclusive: number }
export interface TierRuleset {
  rulesetVersion: string;
  effectiveDate: string;
  description: string;
  categoryValues: Record<string, Record<string, number>>;
  sharedCategoryValues: Record<string, number>;
  evidenceWeights: Record<string, Record<string, number>>;
  excludedSources: string[];
  sourceSubjectOverrides: Record<string, string>;
  tierThresholds: TierThreshold[];
  minDataPoints: number;
  scoreDecimals: number;
  unknownPeriodWeight: number | null;
  percentileFallbackEnabled: boolean;
  ixlPeriodFromDateFallback: boolean;
}

export const configApi = {
  getTierRules: () => request<TierRuleset>('/api/config/tier-rules'),
  putTierRules: (data: Partial<TierRuleset>) =>
    request<TierRuleset>('/api/config/tier-rules', { method: 'PUT', body: JSON.stringify(data) }),
};

export const unmatchedStnsApi = {
  list: () => request<{ total: number; rows: UnmatchedStnRow[] }>('/api/export/unmatched-stns/list'),
};
