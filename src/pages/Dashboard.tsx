import React, { useState, useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { TrendingUp, Award, Users, Target, Info, ChevronRight, ChevronLeft, Pencil, Check, X, Download } from 'lucide-react';
import { studentsApi, Student, dashboardApi, GradeRow, TeacherRow, DrillStudent, TimelinePoint, DashboardKpis, GradeProficiencyRow, GeoZipRow, TierSubject } from '../lib/api';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, Legend, ResponsiveContainer,
  PieChart, Pie, Cell,
  BarChart, Bar
} from 'recharts';
import { MapContainer, TileLayer, CircleMarker, Popup, useMap } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

const defaultCenter: [number, number] = [39.7684, -86.1581];


interface Stats {
  tier1Pct: number;
  tier2Pct: number;
  tier3Pct: number;
  tier1Count: number;
  tier2Count: number;
  tier3Count: number;
  totalStudents: number;
  activeCaseload: number;
  gradeData: { grade: string; proficient: number; developing: number; critical: number }[];
  homeRoomData: { homeRoom: string; initials: string; grade: string; 'Tier 1': number; 'Tier 2': number; 'Tier 3': number; total: number }[];
}

// "-1" = Kindergarten — confirmed by LGS (Velvet Wright) on the 2026-08-14 client demo call.
// Mirrors NormalizeGrade() in DashboardController.cs for client-side rendering.
function normalizeGradeLabel(raw: string | number): string {
  const cleaned = String(raw).trim().toUpperCase();
  if (cleaned === 'K' || cleaned === 'KG' || cleaned === 'KINDERGARTEN' || cleaned === '0' || cleaned === '-1') return 'K';
  return cleaned.replace(/^0+(?=\d)/, '');
}

// "3" -> "3rd", "K" (and anything non-numeric) passes through unchanged.
function ordinalGrade(grade: string): string {
  const n = parseInt(grade, 10);
  if (isNaN(n)) return grade;
  const rem100 = n % 100;
  if (rem100 >= 11 && rem100 <= 13) return `${n}th`;
  switch (n % 10) {
    case 1: return `${n}st`;
    case 2: return `${n}nd`;
    case 3: return `${n}rd`;
    default: return `${n}th`;
  }
}

// "Wood, Ashley" + "3" -> "Wood - 3rd", for the caseload chart's hover tooltip.
function formatTeacherLabel(homeRoom: string, grade: string): string {
  const lastName = homeRoom.includes(',') ? homeRoom.split(',')[0].trim() : homeRoom;
  return grade ? `${lastName} - ${ordinalGrade(grade)}` : lastName;
}

// Single source of truth for tier colors — reused by the donut, the homeroom caseload chart,
// and the grade/teacher breakdown table headers so all three stay visually consistent.
const TIER_COLORS: Record<'Tier 1' | 'Tier 2' | 'Tier 3', string> = {
  'Tier 1': '#214965',
  'Tier 2': '#9ca3af',
  'Tier 3': '#b91c1c',
};

function buildStats(students: Student[], subject: TierSubject): Stats {
  let t1 = 0, t2 = 0, t3 = 0;
  const gradeMap: Record<string, { proficient: number; developing: number; critical: number }> = {};
  const homeRoomMap: Record<string, { tier1: number; tier2: number; tier3: number; grade: string }> = {};

  // Active caseload = all active students regardless of tier status
  const activeCaseload = students.filter(s => s.isActive).length;

  for (const s of students) {
    const subjectTier = subject === 'math' ? s.mathTier : s.elaTier;
    const tier = subjectTier?.tier || 'Pending';
    const tierStatus = subjectTier?.status || 'Pending';
    const grade = s.grade ? `Grade ${normalizeGradeLabel(s.grade)}` : 'Unknown';
    const homeRoom = s.homeRoom || s.classGroup || 'Unassigned';

    // Include any subject that has a system recommendation or has been finalized — gating to
    // Finalized-only would show zero for every chart until every student's subject is
    // individually finalized by an admin.
    if (tierStatus === 'Pending') continue;

    if (tier === 'Tier 1') t1++;
    else if (tier === 'Tier 2') t2++;
    else if (tier === 'Tier 3') t3++;

    if (!homeRoomMap[homeRoom]) homeRoomMap[homeRoom] = { tier1: 0, tier2: 0, tier3: 0, grade: s.grade ? normalizeGradeLabel(s.grade) : '' };
    if (tier === 'Tier 1') homeRoomMap[homeRoom].tier1++;
    else if (tier === 'Tier 2') homeRoomMap[homeRoom].tier2++;
    else if (tier === 'Tier 3') homeRoomMap[homeRoom].tier3++;

    if (grade !== 'Unknown') {
      if (!gradeMap[grade]) gradeMap[grade] = { proficient: 0, developing: 0, critical: 0 };
      if (tier === 'Tier 1') gradeMap[grade].proficient++;
      else if (tier === 'Tier 2') gradeMap[grade].developing++;
      else if (tier === 'Tier 3') gradeMap[grade].critical++;
    }
  }

  const total = t1 + t2 + t3 || 1;

  const gradeData = Object.keys(gradeMap)
    .sort((a, b) => (parseInt(a.replace(/\D/g, '')) || 0) - (parseInt(b.replace(/\D/g, '')) || 0))
    .map(k => {
      const g = gradeMap[k];
      const gt = g.proficient + g.developing + g.critical || 1;
      const proficient = Math.round((g.proficient / gt) * 100);
      const developing = Math.round((g.developing / gt) * 100);
      return {
        grade: k,
        proficient,
        developing,
        critical: 100 - proficient - developing,
      };
    });

  const homeRoomData = Object.keys(homeRoomMap).map(hr => {
    const parts = hr.replace(/,/g, '').split(/\s+/).filter(Boolean);
    const initials = parts.length >= 2
      ? (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
      : hr.substring(0, 2).toUpperCase();
    const m = homeRoomMap[hr];
    return {
      homeRoom: hr,
      initials,
      grade: m.grade,
      'Tier 1': m.tier1,
      'Tier 2': m.tier2,
      'Tier 3': m.tier3,
      total: m.tier1 + m.tier2 + m.tier3,
    };
  }).sort((a, b) => b.total - a.total);

  return {
    tier1Pct: Math.round((t1 / total) * 100),
    tier2Pct: Math.round((t2 / total) * 100),
    tier3Pct: Math.round((t3 / total) * 100),
    tier1Count: t1,
    tier2Count: t2,
    tier3Count: t3,
    totalStudents: t1 + t2 + t3,
    activeCaseload,
    gradeData,
    homeRoomData,
  };
}

// ─── Drill-down types ──────────────────────────────────────────────────────────

type DrillView =
  | { level: 'grades' }
  | { level: 'teachers'; grade: string }
  | { level: 'students'; grade: string; teacher?: string };

export default function Dashboard() {
  const navigate = useNavigate();
  const dashboardRef = useRef<HTMLDivElement>(null);
  const [exportingPdf, setExportingPdf] = useState(false);

  // ELA/Math toggle drives the tier-based charts (donut, grade/teacher tables, homeroom bar, ZIP
  // map). Duplicating all five panels per subject would roughly double the page and break the
  // single-image PDF export, so a single selector switches them instead — except the donut, shown
  // as two compact side-by-side charts since comparing the two distributions at a glance is the
  // main reason a per-subject tier was requested.
  const [tierSubject, setTierSubject] = useState<TierSubject>('ela');
  const allStudentsRef = useRef<Student[]>([]);

  const emptyStats: Stats = {
    tier1Pct: 0, tier2Pct: 0, tier3Pct: 0,
    tier1Count: 0, tier2Count: 0, tier3Count: 0,
    totalStudents: 0,
    activeCaseload: 0,
    gradeData: [],
    homeRoomData: [],
  };
  const [elaStats, setElaStats] = useState<Stats>(emptyStats);
  const [mathStats, setMathStats] = useState<Stats>(emptyStats);
  const stats = tierSubject === 'math' ? mathStats : elaStats;
  const [loadingStats, setLoadingStats] = useState(true);

  // KPIs (real data)
  const [kpis, setKpis] = useState<DashboardKpis | null>(null);
  const [timelineData, setTimelineData] = useState<TimelinePoint[]>([]);

  // Target goal
  const [targetGoal, setTargetGoal] = useState(85);
  const [editingGoal, setEditingGoal] = useState(false);
  const [goalInput, setGoalInput] = useState('85');
  const [savingGoal, setSavingGoal] = useState(false);

  // Drill-down
  const [drillView, setDrillView] = useState<DrillView>({ level: 'grades' });
  const [gradeRows, setGradeRows] = useState<GradeRow[]>([]);
  const [teacherRows, setTeacherRows] = useState<TeacherRow[]>([]);
  const [drillStudents, setDrillStudents] = useState<DrillStudent[]>([]);
  const [loadingDrill, setLoadingDrill] = useState(false);

  // BRD DB-5: grade proficiency bands from actual assessment data
  const [gradeProficiencyData, setGradeProficiencyData] = useState<GradeProficiencyRow[]>([]);

  // BRD DB-12: geographic distribution by ZIP (real data, no simulated socio-economic stats)
  const [geoData, setGeoData] = useState<(GeoZipRow & { lat: number; lng: number })[]>([]);
  const [geoLoading, setGeoLoading] = useState(false);

  useEffect(() => {
    async function fetchAll() {
      setLoadingStats(true);
      try {
        const first = await studentsApi.list({ page: 1, pageSize: 500 });
        const allStudents = first.items;

        if (first.total > 500) {
          const pages = Math.ceil(first.total / 500);
          const rest = await Promise.all(
            Array.from({ length: pages - 1 }, (_, i) =>
              studentsApi.list({ page: i + 2, pageSize: 500 })
            )
          );
          allStudents.push(...rest.flatMap(r => r.items));
        }

        allStudentsRef.current = allStudents;
        setElaStats(buildStats(allStudents, 'ela'));
        setMathStats(buildStats(allStudents, 'math'));
      } catch (e) {
        console.error('Dashboard stats fetch failed', e);
      } finally {
        setLoadingStats(false);
      }
    }

    async function fetchConfig() {
      try {
        const cfg = await dashboardApi.getTargetGoal();
        setTargetGoal(cfg.goalPct);
        setGoalInput(String(cfg.goalPct));
      } catch {
        // use default 85
      }
    }

    async function fetchKpis() {
      try {
        const data = await dashboardApi.kpis();
        setKpis(data);
      } catch (e) {
        console.error('KPI fetch failed', e);
      }
    }

    async function fetchTimeline() {
      try {
        const data = await dashboardApi.timeline();
        setTimelineData(data);
      } catch (e) {
        console.error('Timeline fetch failed', e);
      }
    }

    async function fetchGradeProficiency() {
      try {
        const data = await dashboardApi.byGradeProficiency();
        setGradeProficiencyData(data);
      } catch (e) {
        console.error('Grade proficiency fetch failed', e);
      }
    }

    async function fetchGeographic() {
      setGeoLoading(true);
      try {
        const rows = await dashboardApi.geographic();
        if (rows.length === 0) { setGeoLoading(false); return; }
        // Resolve ZIP codes to coordinates via Nominatim (OpenStreetMap)
        const resolved: (GeoZipRow & { lat: number; lng: number })[] = [];
        for (const row of rows) {
          try {
            const res = await fetch(
              `https://nominatim.openstreetmap.org/search?postalcode=${encodeURIComponent(row.zip)}&countrycodes=us&format=json&limit=1`,
              { headers: { 'Accept-Language': 'en' } }
            );
            const hits = await res.json();
            if (hits.length > 0) {
              resolved.push({ ...row, lat: parseFloat(hits[0].lat), lng: parseFloat(hits[0].lon) });
            }
          } catch { /* skip unresolvable ZIP */ }
        }
        setGeoData(resolved);
      } catch (e) {
        console.error('Geographic fetch failed', e);
      } finally {
        setGeoLoading(false);
      }
    }

    fetchAll();
    fetchConfig();
    fetchKpis();
    fetchTimeline();
    fetchGradeProficiency();
    fetchGeographic();
  }, []);

  // Refetch the grade breakdown, and reset the drill-down to the top level, whenever the
  // ELA/Math toggle changes — a teacher/student drill-down for the previous subject would
  // otherwise keep showing stale tier counts under the new subject.
  useEffect(() => {
    let cancelled = false;
    setDrillView({ level: 'grades' });
    dashboardApi.byGrade(tierSubject)
      .then(rows => { if (!cancelled) setGradeRows(rows); })
      .catch(e => console.error('Grade drill-down fetch failed', e));
    return () => { cancelled = true; };
  }, [tierSubject]);

  async function drillToTeachers(grade: string) {
    setLoadingDrill(true);
    setDrillView({ level: 'teachers', grade });
    try {
      const rows = await dashboardApi.teachersByGrade(grade, tierSubject);
      setTeacherRows(rows);
    } finally {
      setLoadingDrill(false);
    }
  }

  async function drillToStudents(grade: string, teacher?: string) {
    setLoadingDrill(true);
    setDrillView({ level: 'students', grade, teacher });
    try {
      const rows = await dashboardApi.studentsByGrade(grade);
      const filtered = teacher ? rows.filter(s => (s.homeRoom ?? s.classGroup) === teacher) : rows;
      setDrillStudents(filtered);
    } finally {
      setLoadingDrill(false);
    }
  }

  function backToDrillLevel(level: 'grades' | 'teachers') {
    if (level === 'grades') {
      setDrillView({ level: 'grades' });
    } else if (drillView.level === 'students') {
      setDrillView({ level: 'teachers', grade: (drillView as any).grade });
    }
  }

  async function saveGoal() {
    const val = parseInt(goalInput, 10);
    if (isNaN(val) || val < 1 || val > 100) return;
    setSavingGoal(true);
    try {
      await dashboardApi.setTargetGoal(val);
      setTargetGoal(val);
      setEditingGoal(false);
    } catch {
      // keep editing open on error
    } finally {
      setSavingGoal(false);
    }
  }

  async function exportPdf() {
    if (!dashboardRef.current) return;
    setExportingPdf(true);
    try {
      const [{ toPng }, { jsPDF }] = await Promise.all([
        import('html-to-image'),
        import('jspdf'),
      ]);
      // Physically hide excluded elements during capture, then restore.
      const excluded = dashboardRef.current.querySelectorAll<HTMLElement>('[data-pdf-exclude]');
      excluded.forEach((el) => { el.style.visibility = 'hidden'; });
      const dataUrl = await toPng(dashboardRef.current, { pixelRatio: 2 });
      excluded.forEach((el) => { el.style.visibility = ''; });
      const img = new Image();
      img.src = dataUrl;
      await new Promise((res) => { img.onload = res; });
      const pdf = new jsPDF({ unit: 'mm', format: 'a3', orientation: 'landscape' });
      const pageW = pdf.internal.pageSize.getWidth();
      const pageH = pdf.internal.pageSize.getHeight();
      const margin = 10;
      const maxW = pageW - margin * 2;
      const maxH = pageH - margin * 2;
      const ratio = Math.min(maxW / img.width, maxH / img.height);
      const w = img.width * ratio;
      const h = img.height * ratio;
      pdf.addImage(dataUrl, 'PNG', margin + (maxW - w) / 2, margin + (maxH - h) / 2, w, h);
      pdf.save(`lgs-dashboard-${new Date().toISOString().slice(0, 10)}.pdf`);
    } finally {
      setExportingPdf(false);
    }
  }

  const buildDonutData = (s: Stats) => [
    { name: 'Tier 1', value: s.tier1Pct, count: s.tier1Count, color: TIER_COLORS['Tier 1'] },
    { name: 'Tier 2', value: s.tier2Pct, count: s.tier2Count, color: TIER_COLORS['Tier 2'] },
    { name: 'Tier 3', value: s.tier3Pct, count: s.tier3Count, color: TIER_COLORS['Tier 3'] },
  ];
  const elaDonutData = buildDonutData(elaStats);
  const mathDonutData = buildDonutData(mathStats);

  const tierColor: Record<string, string> = { 'Tier 1': 'text-lgs-blue bg-blue-50', 'Tier 2': 'text-slate-600 bg-slate-100', 'Tier 3': 'text-red-700 bg-red-50' };

  return (
    <div ref={dashboardRef} className="space-y-8 max-w-7xl mx-auto pb-12">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-lgs-blue">Performance Trends</h1>
          <p className="text-slate-500 mt-1">Institutional academic growth and intervention analytics for the 2024-2025 school year.</p>
        </div>
        <button
          onClick={exportPdf}
          disabled={exportingPdf}
          data-pdf-exclude="true"
          className="flex items-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-semibold rounded-xl hover:bg-blue-900 transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
        >
          <Download className="w-4 h-4" />
          {exportingPdf ? 'Generating PDF…' : 'Export PDF'}
        </button>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-6">
        <KpiCard
          icon={<TrendingUp className="w-6 h-6 text-lgs-blue" />}
          iconBg="bg-slate-100"
          badge={kpis?.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? '↗ IMPROVED' : '↘ DECLINED') : '— NO DATA'}
          badgeColor={kpis?.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? 'text-green-600 bg-green-50' : 'text-lgs-red bg-red-50') : 'text-slate-400 bg-slate-100'}
          label="Avg. ELA Growth"
          value={kpis == null ? '...' : kpis.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? `+${kpis.elaGrowthAvgDelta}` : String(kpis.elaGrowthAvgDelta)) : 'N/A'}
          sub={kpis == null ? '' : kpis.elaStudentsWithGrowthData > 0 ? `${kpis.elaStudentsWithGrowthData} students with 2+ scores on the same test` : 'Need 2+ IXL or 2+ ILEARN ELA scores'}
          tooltip="Average change in raw ELA score (latest minus earliest), only when both scores come from the same assessment. IXL and ILEARN scales are not comparable."
        />
        <KpiCard
          icon={<TrendingUp className="w-6 h-6 text-lgs-red" />}
          iconBg="bg-red-50"
          badge={kpis?.mathGrowthAvgDelta != null ? (kpis.mathGrowthAvgDelta >= 0 ? '↗ IMPROVED' : '↘ DECLINED') : '— NO DATA'}
          badgeColor={kpis?.mathGrowthAvgDelta != null ? (kpis.mathGrowthAvgDelta >= 0 ? 'text-green-600 bg-green-50' : 'text-lgs-red bg-red-50') : 'text-slate-400 bg-slate-100'}
          label="Avg. Math Growth"
          value={kpis == null ? '...' : kpis.mathGrowthAvgDelta != null ? (kpis.mathGrowthAvgDelta >= 0 ? `+${kpis.mathGrowthAvgDelta}` : String(kpis.mathGrowthAvgDelta)) : 'N/A'}
          sub={kpis == null ? '' : kpis.mathStudentsWithGrowthData > 0 ? `${kpis.mathStudentsWithGrowthData} students with 2+ scores on the same test` : 'Need 2+ IXL or 2+ ILEARN Math scores'}
          tooltip="Average change in raw Math score (latest minus earliest), only when both scores come from the same assessment. IXL and ILEARN scales are not comparable."
        />
        <KpiCard
          icon={<Users className="w-6 h-6 text-lgs-blue" />}
          iconBg="bg-slate-100"
          label="Active Caseload"
          value={loadingStats ? '...' : String(stats.activeCaseload)}
          sub={loadingStats ? '' : `${stats.tier3Count} students in Tier 3`}
          tooltip="Total number of active students in the system. Tier distribution excludes students with Pending tier status. No target threshold has been set for this metric, so no status badge is shown — tell us what caseload or Tier 3 share should count as a concern and we can add one."
        />
        <KpiCard
          icon={<Award className="w-6 h-6 text-lgs-blue" />}
          iconBg="bg-slate-100"
          label="ELA Proficiency"
          value={kpis == null ? '...' : kpis.elaProficiencyPct != null ? `${kpis.elaProficiencyPct}%` : 'N/A'}
          sub={kpis == null ? '' : kpis.elaStudentsTotal > 0 ? `${kpis.elaStudentsOnAbove} of ${kpis.elaStudentsTotal} students On/Above` : 'No ELA assessment data'}
          tooltip="Percentage of students with latest ELA assessment at or above grade level (On/Above). Source: ILEARN & IXL."
        />
        <KpiCard
          icon={<Award className="w-6 h-6 text-lgs-red" />}
          iconBg="bg-red-50"
          label="Math Proficiency"
          value={kpis == null ? '...' : kpis.mathProficiencyPct != null ? `${kpis.mathProficiencyPct}%` : 'N/A'}
          sub={kpis == null ? '' : kpis.mathStudentsTotal > 0 ? `${kpis.mathStudentsOnAbove} of ${kpis.mathStudentsTotal} students On/Above` : 'No Math assessment data'}
          tooltip="Percentage of students with latest Math assessment at or above grade level (On/Above). Source: ILEARN & IXL."
        />
        {/* Target Goal — editable */}
        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-slate-100 flex items-center justify-center"><Target className="w-6 h-6 text-lgs-blue" /></div>
          </div>
          <div className="flex items-center gap-1 mb-1">
            <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">Target Goal</h3>
          </div>
          {editingGoal ? (
            <div className="flex items-center gap-2 mt-1">
              <input
                type="number"
                min={1} max={100}
                value={goalInput}
                onChange={e => setGoalInput(e.target.value)}
                className="w-20 border border-slate-300 rounded-lg px-2 py-1 text-xl font-black text-lgs-blue focus:outline-none focus:ring-2 focus:ring-lgs-blue"
                autoFocus
              />
              <span className="text-xl font-black text-lgs-blue">%</span>
              <button onClick={saveGoal} disabled={savingGoal} className="p-1 rounded-lg bg-green-100 text-green-700 hover:bg-green-200 disabled:opacity-50"><Check className="w-4 h-4" /></button>
              <button onClick={() => { setEditingGoal(false); setGoalInput(String(targetGoal)); }} className="p-1 rounded-lg bg-slate-100 text-slate-500 hover:bg-slate-200"><X className="w-4 h-4" /></button>
            </div>
          ) : (
            <div className="flex items-center gap-2">
              <div className="text-3xl font-black text-lgs-blue">{targetGoal}%</div>
              <button onClick={() => setEditingGoal(true)} className="p-1 rounded-lg hover:bg-slate-100 text-slate-400 hover:text-lgs-blue transition-colors"><Pencil className="w-3.5 h-3.5" /></button>
            </div>
          )}
          <p className="text-xs text-slate-500 font-medium mt-1">Proficiency objective</p>
        </div>
      </div>

      {/* Charts Row 1 */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100 lg:col-span-2 flex flex-col">
          <div className="mb-6">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <TrendingUp className="w-5 h-5 text-lgs-red" />
              Academic Growth Timeline
            </h2>
            <p className="text-sm text-slate-500 mt-1">Monthly average proficiency (0–3) for ELA and Math. Below = 0, Approaching = 1, On = 2, Above = 3.</p>
          </div>
          <div className="flex-1 min-h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={timelineData} margin={{ top: 10, right: 10, left: -20, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                <XAxis dataKey="month" axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} dy={10} />
                <YAxis axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} domain={[0, 3]} ticks={[0, 1, 2, 3]} />
                <RechartsTooltip contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} itemStyle={{ fontSize: '12px', fontWeight: 500 }} labelStyle={{ fontSize: '12px', color: '#64748b' }} />
                <Legend iconType="square" wrapperStyle={{ fontSize: '12px', paddingTop: '20px' }} />
                <Line type="monotone" name="ELA" dataKey="ela" stroke="#214965" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
                <Line type="monotone" name="Math" dataKey="math" stroke="#b91c1c" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </div>

        <div className="bg-[#fff5f5] p-6 rounded-2xl border border-red-50 flex flex-col">
          <div className="mb-2">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <Users className="w-5 h-5 text-lgs-red" />
              Tier Distribution
            </h2>
            {/* Two subject tiers are calculated independently (TR-011) — shown side by side so
                ELA and Math distributions can be compared at a glance, which is the primary
                reason a per-subject tier was requested. Includes System Recommended tiers. */}
            <p className="text-sm text-slate-500 mt-1">ELA vs. Math tier distribution across system-recommended and admin-overridden students.</p>
          </div>
          <div className="flex-1 grid grid-cols-2 gap-2 min-h-[180px]">
            {([['ELA', elaDonutData], ['Math', mathDonutData]] as const).map(([label, data]) => (
              <div key={label} className="flex flex-col">
                <p className="text-center text-xs font-bold text-slate-500 uppercase tracking-wide mb-1">{label}</p>
                <div className="flex-1">
                  <ResponsiveContainer width="100%" height="100%">
                    <PieChart>
                      <Pie data={data} cx="50%" cy="50%" innerRadius={38} outerRadius={54} paddingAngle={2} dataKey="value" stroke="none">
                        {data.map((entry, i) => <Cell key={i} fill={entry.color} />)}
                      </Pie>
                      <RechartsTooltip formatter={(v: number, name: string, p: any) => [`${v}% (${p.payload.count} students)`, name]} contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} />
                    </PieChart>
                  </ResponsiveContainer>
                </div>
              </div>
            ))}
          </div>
          <div className="space-y-2 mt-4">
            {(['Tier 1', 'Tier 2', 'Tier 3'] as const).map(tierName => {
              const ela = elaDonutData.find(d => d.name === tierName)!;
              const math = mathDonutData.find(d => d.name === tierName)!;
              return (
                <div key={tierName} className="flex items-center justify-between text-xs font-bold">
                  <div className="flex items-center gap-2 text-lgs-blue">
                    <div className="w-2.5 h-2.5 rounded-full" style={{ backgroundColor: ela.color }} />
                    {tierName}
                  </div>
                  <div className="text-lgs-blue">
                    ELA {ela.count} <span className="text-slate-400 font-normal">·</span> Math {math.count}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Grade-level breakdown — BRD DB-5: proficiency bands from assessment data */}
      {gradeProficiencyData.length > 0 && (
        <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
          <div className="mb-6">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <Award className="w-5 h-5 text-slate-400" />
              Grade-Level Proficiency Summary
            </h2>
            <p className="text-sm text-slate-500 mt-1">Academic status breakdown by grade level for the current assessment window.</p>
          </div>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={gradeProficiencyData} layout="vertical" margin={{ top: 0, right: 0, left: 0, bottom: 0 }} barSize={24}>
                <XAxis type="number" domain={[0, 100]} hide />
                <YAxis dataKey="grade" type="category" axisLine={false} tickLine={false} tick={{ fill: '#214965', fontSize: 12, fontWeight: 600 }} width={80} tickFormatter={ordinalGrade} />
                <RechartsTooltip
                  formatter={(v: number, name: string) => [`${v}%`, name]}
                  labelFormatter={(label: string) => ordinalGrade(label)}
                  contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                />
                <Legend
                  wrapperStyle={{ fontSize: '12px', paddingTop: '10px' }}
                  content={
                    <StaticLegend
                      items={[
                        { label: 'Above', color: '#15803d' },
                        { label: 'On Grade', color: '#214965' },
                        { label: 'Approaching', color: '#d97706' },
                        { label: 'Below', color: '#b91c1c' },
                      ]}
                    />
                  }
                />
                <Bar dataKey="below" name="Below" stackId="a" fill="#b91c1c" radius={[4, 0, 0, 4]} />
                <Bar dataKey="approaching" name="Approaching" stackId="a" fill="#d97706" />
                <Bar dataKey="on" name="On Grade" stackId="a" fill="#214965" />
                <Bar dataKey="above" name="Above" stackId="a" fill="#15803d" radius={[0, 4, 4, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      )}

      {/* Grade → Teacher → Student drill-down */}
      <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
        <div className="mb-4 flex items-start justify-between gap-3">
          <div className="flex items-center gap-3">
            {drillView.level !== 'grades' && (
              <button onClick={() => backToDrillLevel(drillView.level === 'students' ? 'teachers' : 'grades')} className="p-1.5 rounded-lg hover:bg-slate-200 text-slate-500 hover:text-lgs-blue transition-colors">
                <ChevronLeft className="w-5 h-5" />
              </button>
            )}
            <div>
              <h2 className="text-lg font-bold text-lgs-blue uppercase tracking-wide flex items-center gap-2">
                <Users className="w-5 h-5 text-slate-400" />
                {drillView.level === 'grades' && 'Grade Breakdown'}
                {drillView.level === 'teachers' && `Grade ${(drillView as any).grade} — By Teacher`}
                {drillView.level === 'students' && `Grade ${(drillView as any).grade} — Students`}
              </h2>
              {/* Math and ELA tiers are calculated and finalized independently (TR-011) — this
                  section, the homeroom bar, and the ZIP map all reflect whichever subject is
                  selected below. Includes System Recommended tiers, not just Finalized ones. */}
              <p className="text-xs text-slate-400 -mt-0.5 mb-1">{tierSubject === 'math' ? 'Math' : 'ELA'} tier — system-recommended or admin-overridden</p>
            {/* Breadcrumb */}
            <p className="text-sm text-slate-500 flex items-center gap-1 mt-0.5">
              <span
                className={drillView.level !== 'grades' ? 'cursor-pointer hover:underline text-lgs-blue' : ''}
                onClick={() => drillView.level !== 'grades' && setDrillView({ level: 'grades' })}
              >All Grades</span>
              {drillView.level !== 'grades' && (
                <>
                  <ChevronRight className="w-3.5 h-3.5" />
                  <span
                    className={drillView.level === 'students' ? 'cursor-pointer hover:underline text-lgs-blue' : ''}
                    onClick={() => drillView.level === 'students' && setDrillView({ level: 'teachers', grade: (drillView as any).grade })}
                  >Grade {(drillView as any).grade}</span>
                </>
              )}
              {drillView.level === 'students' && (drillView as any).teacher && (
                <>
                  <ChevronRight className="w-3.5 h-3.5" />
                  <span>{(drillView as any).teacher}</span>
                </>
              )}
            </p>
            </div>
          </div>

          {/* ELA/Math toggle — drives this table, the homeroom bar, and the ZIP map below */}
          <div className="flex rounded-lg border border-slate-200 bg-white p-0.5 shrink-0" data-pdf-exclude="true">
            {(['ela', 'math'] as TierSubject[]).map(subj => (
              <button
                key={subj}
                onClick={() => setTierSubject(subj)}
                className={`px-3 py-1.5 text-xs font-bold uppercase tracking-wide rounded-md transition-colors ${
                  tierSubject === subj ? 'bg-lgs-blue text-white' : 'text-slate-500 hover:text-lgs-blue'
                }`}
              >
                {subj === 'ela' ? 'ELA' : 'Math'}
              </button>
            ))}
          </div>
        </div>

        {loadingDrill ? (
          <div className="text-slate-400 text-sm py-8 text-center">Loading…</div>
        ) : drillView.level === 'grades' ? (
          gradeRows.length === 0 ? (
            <div className="text-slate-400 text-sm py-8 text-center">No tiered students yet.</div>
          ) : (
            <>
              <p className="text-xs text-slate-400 mb-3 italic">
                Tier 1/2/3 counts reflect tiered students only. Pending is students awaiting a tier determination, so Total covers every student in the grade.
              </p>
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs font-bold text-slate-400 uppercase tracking-wider border-b border-slate-200">
                    <th className="pb-3 pr-4">Grade</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 3'] }}>Tier 3</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 2'] }}>Tier 2</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 1'] }}>Tier 1</th>
                    <th className="pb-3 pr-4">Pending</th>
                    <th className="pb-3 pr-4">Total</th>
                    <th className="pb-3" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {gradeRows.map(row => (
                    <tr key={row.grade} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => drillToTeachers(row.grade)}>
                      <td className="py-3 pr-4 font-semibold text-lgs-blue">Grade {row.grade}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 3'] }}>{row.tier3}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 2'] }}>{row.tier2}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 1'] }}>{row.tier1}</td>
                      <td className="py-3 pr-4 text-slate-400">{row.pending}</td>
                      <td className="py-3 pr-4">{row.total}</td>
                      <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )
        ) : drillView.level === 'teachers' ? (
          teacherRows.length === 0 ? (
            <div className="text-slate-400 text-sm py-8 text-center">No teachers found for this grade.</div>
          ) : (
            <>
              <p className="text-xs text-slate-400 mb-3 italic">
                Tier 1/2/3 counts reflect tiered students only. Pending is students awaiting a tier determination, so Total covers every student in the class.
              </p>
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs font-bold text-slate-400 uppercase tracking-wider border-b border-slate-200">
                    <th className="pb-3 pr-4">Teacher / Class</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 3'] }}>Tier 3</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 2'] }}>Tier 2</th>
                    <th className="pb-3 pr-4" style={{ color: TIER_COLORS['Tier 1'] }}>Tier 1</th>
                    <th className="pb-3 pr-4">Pending</th>
                    <th className="pb-3 pr-4">Total</th>
                    <th className="pb-3" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {teacherRows.map(row => (
                    <tr key={row.teacher} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => drillToStudents((drillView as any).grade, row.teacher)}>
                      <td className="py-3 pr-4 font-semibold text-lgs-blue">{row.teacher}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 3'] }}>{row.tier3}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 2'] }}>{row.tier2}</td>
                      <td className="py-3 pr-4 font-semibold" style={{ color: TIER_COLORS['Tier 1'] }}>{row.tier1}</td>
                      <td className="py-3 pr-4 text-slate-400">{row.pending}</td>
                      <td className="py-3 pr-4">{row.total}</td>
                      <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                    </tr>
                  ))}
                  <tr className="hover:bg-slate-100 transition-colors cursor-pointer text-slate-400" onClick={() => drillToStudents((drillView as any).grade)}>
                    <td className="py-3 pr-4 italic">View all students in grade</td>
                    <td colSpan={5} />
                    <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                  </tr>
                </tbody>
              </table>
            </>
          )
        ) : (
          drillStudents.length === 0 ? (
            <div className="text-slate-400 text-sm py-8 text-center">No students found.</div>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-xs font-bold text-slate-400 uppercase tracking-wider border-b border-slate-200">
                  <th className="pb-3 pr-4">Name</th>
                  <th className="pb-3 pr-4">Class Group</th>
                  <th className="pb-3 pr-4">Tier</th>
                  <th className="pb-3">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {drillStudents.map(s => {
                  const tier = tierSubject === 'math' ? s.mathTier : s.elaTier;
                  const status = tierSubject === 'math' ? s.mathTierStatus : s.elaTierStatus;
                  return (
                    <tr key={s.studentId} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => navigate(`/students/${s.studentId}`)}>
                      <td className="py-3 pr-4 font-semibold text-lgs-blue">{s.fullName}</td>
                      <td className="py-3 pr-4 text-slate-500">{s.classGroup || '—'}</td>
                      <td className="py-3 pr-4">
                        <span className={`text-xs font-bold px-2 py-0.5 rounded-full ${tierColor[tier ?? ''] ?? 'text-slate-400 bg-slate-100'}`}>{tier || 'Pending'}</span>
                      </td>
                      <td className="py-3 text-slate-500 text-xs">{status}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )
        )}
      </div>

      {/* Home room + map */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {stats.homeRoomData.length > 0 && (
          <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
            <div className="mb-6">
              <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
                <Users className="w-5 h-5 text-slate-400" />
                Caseload by Home Room
              </h2>
              <p className="text-sm text-slate-500 mt-1">Distribution of {tierSubject === 'math' ? 'Math' : 'ELA'} Tier 1 / Tier 2 / Tier 3 students per homeroom teacher.</p>
            </div>
            <div className="h-[400px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={stats.homeRoomData} layout="vertical" margin={{ top: 10, right: 30, left: 10, bottom: 10 }} barSize={16}>
                  <CartesianGrid strokeDasharray="3 3" horizontal={false} stroke="#e2e8f0" />
                  <XAxis type="number" axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} />
                  <YAxis dataKey="initials" type="category" axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} width={40} interval={0} />
                  <RechartsTooltip content={<CaseloadTooltip />} cursor={{ fill: '#f1f5f9' }} />
                  <Legend
                    wrapperStyle={{ paddingTop: '20px' }}
                    content={
                      <StaticLegend
                        items={[
                          { label: 'Tier 1', color: TIER_COLORS['Tier 1'] },
                          { label: 'Tier 2', color: TIER_COLORS['Tier 2'] },
                          { label: 'Tier 3', color: TIER_COLORS['Tier 3'] },
                        ]}
                      />
                    }
                  />
                  <Bar dataKey="Tier 1" stackId="a" fill={TIER_COLORS['Tier 1']} />
                  <Bar dataKey="Tier 2" stackId="a" fill={TIER_COLORS['Tier 2']} />
                  <Bar dataKey="Tier 3" stackId="a" fill={TIER_COLORS['Tier 3']} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>
        )}

        <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
          <div className="mb-6">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <Target className="w-5 h-5 text-slate-400" />
              Geographic Distribution
            </h2>
            <p className="text-sm text-slate-500 mt-1">{tierSubject === 'math' ? 'Math' : 'ELA'} tier distribution by ZIP code. Circle size reflects student count; color reflects tier mix.</p>
          </div>
          <div className="h-[400px] relative rounded-xl overflow-hidden border border-slate-200 z-0">
            <MapContainer center={defaultCenter} zoom={10} style={{ height: '100%', width: '100%' }}>
              <TileLayer
                attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
                url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
              />
              {geoData.map(row => {
                const tier1 = tierSubject === 'math' ? row.mathTier1 : row.elaTier1;
                const tier2 = tierSubject === 'math' ? row.mathTier2 : row.elaTier2;
                const tier3 = tierSubject === 'math' ? row.mathTier3 : row.elaTier3;
                return (
                  <CircleMarker
                    key={row.zip}
                    center={[row.lat, row.lng]}
                    radius={Math.max(6, Math.min(30, row.total * 1.5))}
                    pathOptions={{
                      color: tier3 > tier1 ? '#b91c1c' : '#214965',
                      fillColor: tier3 > tier1 ? '#b91c1c' : '#214965',
                      fillOpacity: 0.6,
                      weight: 1,
                    }}
                  >
                    <Popup>
                      <div className="text-sm">
                        <p className="font-semibold mb-1">ZIP: {row.zip}</p>
                        <p>Total students: {row.total}</p>
                        <p>{tierSubject === 'math' ? 'Math' : 'ELA'} — Tier 1: {tier1} &nbsp; Tier 2: {tier2} &nbsp; Tier 3: {tier3}</p>
                      </div>
                    </Popup>
                  </CircleMarker>
                );
              })}
              {geoData.length > 0 && <GeoMapFitBounds data={geoData} />}
            </MapContainer>
          </div>
          {geoLoading && (
            <p className="text-xs text-slate-400 mt-2 italic">Resolving ZIP code coordinates…</p>
          )}
          {!geoLoading && geoData.length === 0 && (
            <p className="text-xs text-slate-400 mt-2 italic">No ZIP code data available. Upload demographics files containing a Zip Code column to populate this map.</p>
          )}
          {!geoLoading && geoData.length > 0 && (
            <p className="text-xs text-slate-400 mt-2">
              {geoData.length} ZIP code{geoData.length !== 1 ? 's' : ''} · Coordinates via OpenStreetMap Nominatim
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

// Fixed-order legend — Recharts' default Legend doesn't reliably preserve the order bars were
// declared in, so charts that must read worst-to-best left-to-right build their legend from this
// instead of the built-in auto-generated one.
function StaticLegend({ items }: { items: { label: string; color: string }[] }) {
  return (
    <ul className="flex flex-wrap justify-center gap-4 text-xs font-medium text-slate-600">
      {items.map(item => (
        <li key={item.label} className="flex items-center gap-1.5">
          <span className="inline-block w-2.5 h-2.5" style={{ backgroundColor: item.color }} />
          {item.label}
        </li>
      ))}
    </ul>
  );
}

// Custom tooltip for the Caseload by Home Room chart — replaces the default label (the
// homeroom's initials, shown on the axis) with "Last name - Nth grade" on hover.
function CaseloadTooltip({ active, payload }: { active?: boolean; payload?: any[] }) {
  if (!active || !payload || payload.length === 0) return null;
  const row = payload[0].payload as { homeRoom: string; grade: string; 'Tier 1': number; 'Tier 2': number; 'Tier 3': number };
  return (
    <div className="bg-white rounded-lg p-3 text-xs" style={{ boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}>
      <p className="font-semibold text-slate-700 mb-1">{formatTeacherLabel(row.homeRoom, row.grade)}</p>
      <p className="font-medium" style={{ color: TIER_COLORS['Tier 1'] }}>Tier 1: {row['Tier 1']}</p>
      <p className="font-medium" style={{ color: TIER_COLORS['Tier 2'] }}>Tier 2: {row['Tier 2']}</p>
      <p className="font-medium" style={{ color: TIER_COLORS['Tier 3'] }}>Tier 3: {row['Tier 3']}</p>
    </div>
  );
}

function GeoMapFitBounds({ data }: { data: { lat: number; lng: number }[] }) {
  const map = useMap();
  useEffect(() => {
    if (data.length === 0) return;
    const bounds = data.map(d => [d.lat, d.lng] as [number, number]);
    map.fitBounds(bounds, { padding: [40, 40] });
  }, [data, map]);
  return null;
}

function KpiCard({ icon, iconBg, badge, badgeColor, label, value, sub, tooltip }: {
  icon: React.ReactNode;
  iconBg: string;
  badge?: string;
  badgeColor?: string;
  label: string;
  value: string;
  sub: string;
  tooltip: string;
}) {
  const [showTooltip, setShowTooltip] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Hover works for mouse users; tap-to-toggle (with an outside-click/tap to dismiss) covers
  // touch devices like iPad, which have no :hover state and never triggered the old
  // group-hover-only tooltip.
  useEffect(() => {
    if (!showTooltip) return;
    function handleOutside(e: MouseEvent | TouchEvent) {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setShowTooltip(false);
    }
    document.addEventListener('mousedown', handleOutside);
    document.addEventListener('touchstart', handleOutside);
    return () => {
      document.removeEventListener('mousedown', handleOutside);
      document.removeEventListener('touchstart', handleOutside);
    };
  }, [showTooltip]);

  return (
    <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
      <div className="flex justify-between items-start mb-4">
        <div className={`w-12 h-12 rounded-xl ${iconBg} flex items-center justify-center`}>{icon}</div>
        {badge && <span className={`text-xs font-bold flex items-center gap-1 px-2 py-1 rounded-md ${badgeColor}`}>{badge}</span>}
      </div>
      <div ref={wrapRef} className="flex items-center gap-1 mb-1 relative">
        <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">{label}</h3>
        <button
          type="button"
          onClick={() => setShowTooltip(v => !v)}
          onMouseEnter={() => setShowTooltip(true)}
          onMouseLeave={() => setShowTooltip(false)}
          aria-label={`About ${label}`}
          aria-expanded={showTooltip}
          className="text-slate-400 hover:text-slate-600"
        >
          <Info className="w-3.5 h-3.5 cursor-help" />
        </button>
        {showTooltip && (
          <div className="absolute bottom-full left-0 mb-2 w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 normal-case tracking-normal">
            {tooltip}
            <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800" />
          </div>
        )}
      </div>
      <div className="text-3xl font-black text-lgs-blue mb-1">{value}</div>
      <p className="text-xs text-slate-500 font-medium">{sub}</p>
    </div>
  );
}
