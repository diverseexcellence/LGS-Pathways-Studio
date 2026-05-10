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

export interface Student {
  studentId: string;
  fullName: string;
  dob: string;
  classGroup: string;
  enrolDate: string;
  isActive: boolean;
  tier?: string;
  tierStatus?: string;
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
  fileName?: string;
}

export interface Assessment {
  id: string;
  studentId: string;
  subject: string;
  score: number | null;
  proficiency: string | null;
  period: string | null;
  uploadType: string;
  date: string | null;
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
};

// ─── Assessments ──────────────────────────────────────────────────────────────

export const assessmentsApi = {
  byStudent: (studentId: string) =>
    request<Assessment[]>(`/api/assessments?studentId=${studentId}`),

  bySubject: (studentId: string, subject: string) =>
    request<Assessment[]>(`/api/assessments?studentId=${studentId}&subject=${subject}`),
};

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

  importLandingZone: () =>
    request<{ message: string; results: { file: string; uploadType?: string; result?: ParseSummary; error?: string }[] }>(
      '/api/upload/import-landing-zone',
      { method: 'POST' }
    ),
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
