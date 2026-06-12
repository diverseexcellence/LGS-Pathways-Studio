import React, { useState, useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { TrendingUp, Award, Users, Target, Info, ChevronRight, ChevronLeft, Pencil, Check, X, Download } from 'lucide-react';
import { studentsApi, Student, dashboardApi, GradeRow, TeacherRow, DrillStudent, TimelinePoint, DashboardKpis } from '../lib/api';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, Legend, ResponsiveContainer,
  PieChart, Pie, Cell,
  BarChart, Bar
} from 'recharts';
import { MapContainer, TileLayer } from 'react-leaflet';
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
  homeRoomData: { homeRoom: string; initials: string; 'Tier 1': number; 'Tier 2': number; 'Tier 3': number; total: number }[];
}

function buildStats(students: Student[]): Stats {
  let t1 = 0, t2 = 0, t3 = 0;
  const gradeMap: Record<string, { proficient: number; developing: number; critical: number }> = {};
  const homeRoomMap: Record<string, { tier1: number; tier2: number; tier3: number }> = {};

  // Active caseload = all active students regardless of tier status
  const activeCaseload = students.filter(s => s.isActive).length;

  for (const s of students) {
    const tier = s.tier || 'Pending';
    const tierStatus = s.tierStatus || 'Pending';
    const grade = s.grade ? `Grade ${String(s.grade).replace(/^0+(?=\d)/, '')}` : 'Unknown';
    const homeRoom = s.classGroup || 'Unassigned';

    // Only count tiered (non-Pending tierStatus) students in distribution aggregations
    if (tierStatus === 'Pending') continue;

    if (tier === 'Tier 1') t1++;
    else if (tier === 'Tier 2') t2++;
    else if (tier === 'Tier 3') t3++;

    if (!homeRoomMap[homeRoom]) homeRoomMap[homeRoom] = { tier1: 0, tier2: 0, tier3: 0 };
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
      return {
        grade: k,
        proficient: Math.round((g.proficient / gt) * 100),
        developing: Math.round((g.developing / gt) * 100),
        critical: Math.round((g.critical / gt) * 100),
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
  const [stats, setStats] = useState<Stats>({
    tier1Pct: 0, tier2Pct: 0, tier3Pct: 0,
    tier1Count: 0, tier2Count: 0, tier3Count: 0,
    totalStudents: 0,
    activeCaseload: 0,
    gradeData: [],
    homeRoomData: [],
  });
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

        setStats(buildStats(allStudents));
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

    async function fetchGrades() {
      try {
        const rows = await dashboardApi.byGrade();
        setGradeRows(rows);
      } catch (e) {
        console.error('Grade drill-down fetch failed', e);
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

    fetchAll();
    fetchConfig();
    fetchGrades();
    fetchKpis();
    fetchTimeline();
  }, []);

  async function drillToTeachers(grade: string) {
    setLoadingDrill(true);
    setDrillView({ level: 'teachers', grade });
    try {
      const rows = await dashboardApi.teachersByGrade(grade);
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
      const html2pdf = (await import('html2pdf.js')).default;
      // html2canvas can't parse oklch() (used by Tailwind v4 CSS vars).
      // Inline computed rgb() values on every element in the cloned doc before capture.
      await html2pdf()
        .set({
          margin: [10, 10, 10, 10],
          filename: `lgs-dashboard-${new Date().toISOString().slice(0, 10)}.pdf`,
          image: { type: 'jpeg', quality: 0.95 },
          html2canvas: {
            scale: 2,
            useCORS: true,
            logging: false,
            onclone: (_doc: Document, el: HTMLElement) => {
              const colorProps = [
                'color', 'backgroundColor', 'borderColor', 'borderTopColor',
                'borderRightColor', 'borderBottomColor', 'borderLeftColor',
                'outlineColor', 'fill', 'stroke',
              ] as const;
              const tmp = document.createElement('div');
              tmp.style.display = 'none';
              document.body.appendChild(tmp);
              el.querySelectorAll<HTMLElement>('*').forEach((node) => {
                const computed = window.getComputedStyle(node);
                colorProps.forEach((prop) => {
                  const val = computed[prop as keyof CSSStyleDeclaration] as string;
                  if (val && val.includes('oklch')) {
                    tmp.style.color = val;
                    node.style[prop as keyof CSSStyleDeclaration] = window.getComputedStyle(tmp).color as never;
                  }
                });
              });
              document.body.removeChild(tmp);
            },
          },
          jsPDF: { unit: 'mm', format: 'a3', orientation: 'landscape' },
        })
        .from(dashboardRef.current)
        .save();
    } finally {
      setExportingPdf(false);
    }
  }

  const donutData = [
    { name: 'Tier 1', value: stats.tier1Pct, count: stats.tier1Count, color: '#214965' },
    { name: 'Tier 2', value: stats.tier2Pct, count: stats.tier2Count, color: '#9ca3af' },
    { name: 'Tier 3', value: stats.tier3Pct, count: stats.tier3Count, color: '#b91c1c' },
  ];

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
          className="flex items-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-semibold rounded-xl hover:bg-blue-900 transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
        >
          <Download className="w-4 h-4" />
          {exportingPdf ? 'Generating PDF…' : 'Export PDF'}
        </button>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6">
        <KpiCard
          icon={<TrendingUp className="w-6 h-6 text-lgs-blue" />}
          iconBg="bg-slate-100"
          badge={kpis?.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? '↗ IMPROVED' : '↘ DECLINED') : '— NO DATA'}
          badgeColor={kpis?.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? 'text-green-600 bg-green-50' : 'text-lgs-red bg-red-50') : 'text-slate-400 bg-slate-100'}
          label="Avg. ELA Growth"
          value={kpis == null ? '...' : kpis.elaGrowthAvgDelta != null ? (kpis.elaGrowthAvgDelta >= 0 ? `+${kpis.elaGrowthAvgDelta}` : String(kpis.elaGrowthAvgDelta)) : 'N/A'}
          sub={kpis == null ? '' : kpis.elaStudentsWithGrowthData > 0 ? `${kpis.elaStudentsWithGrowthData} students with 2+ assessments` : 'Insufficient data for growth calc'}
          tooltip="Average score delta (latest minus earliest ELA assessment) across students with 2+ ELA records. Source: ILEARN & IXL."
        />
        <KpiCard
          icon={<Award className="w-6 h-6 text-lgs-red" />}
          iconBg="bg-red-50"
          badge={kpis?.mathProficiencyPct != null ? (kpis.mathProficiencyPct >= 50 ? '↗ IMPROVED' : '↘ ALERT') : '— NO DATA'}
          badgeColor={kpis?.mathProficiencyPct != null ? (kpis.mathProficiencyPct >= 50 ? 'text-green-600 bg-green-50' : 'text-lgs-red bg-red-50') : 'text-slate-400 bg-slate-100'}
          label="Math Proficiency"
          value={kpis == null ? '...' : kpis.mathProficiencyPct != null ? `${kpis.mathProficiencyPct}%` : 'N/A'}
          sub={kpis == null ? '' : kpis.mathStudentsTotal > 0 ? `${kpis.mathStudentsOnAbove} of ${kpis.mathStudentsTotal} students On/Above` : 'No Math assessment data'}
          tooltip="Percentage of students with latest Math assessment at or above grade level (On/Above). Source: ILEARN & IXL."
        />
        <KpiCard icon={<Users className="w-6 h-6 text-lgs-blue" />} iconBg="bg-slate-100" badge="↘ ALERT" badgeColor="text-lgs-red bg-red-50" label="Active Caseload" value={loadingStats ? '...' : String(stats.activeCaseload)} sub={loadingStats ? '' : `${stats.tier3Count} students in Tier 3`} tooltip="Total number of active students in the system. Tier distribution excludes students with Pending tier status." />
        {/* Target Goal — editable */}
        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-slate-100 flex items-center justify-center"><Target className="w-6 h-6 text-lgs-blue" /></div>
            <span className="text-xs font-bold flex items-center gap-1 px-2 py-1 rounded-md text-green-600 bg-green-50">↗ IMPROVED</span>
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
            <p className="text-sm text-slate-500 mt-1">Monthly average growth percentiles across ELA and Mathematics.</p>
          </div>
          <div className="flex-1 min-h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={timelineData} margin={{ top: 10, right: 10, left: -20, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                <XAxis dataKey="month" axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} dy={10} />
                <YAxis axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} domain={[0, 80]} ticks={[0, 20, 40, 60, 80]} />
                <RechartsTooltip contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} itemStyle={{ fontSize: '12px', fontWeight: 500 }} labelStyle={{ fontSize: '12px', color: '#64748b' }} />
                <Legend iconType="square" wrapperStyle={{ fontSize: '12px', paddingTop: '20px' }} />
                <Line type="monotone" name="ELA Growth" dataKey="ela" stroke="#214965" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
                <Line type="monotone" name="Math Growth" dataKey="math" stroke="#b91c1c" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </div>

        <div className="bg-[#fff5f5] p-6 rounded-2xl border border-red-50 flex flex-col">
          <div className="mb-2">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <Users className="w-5 h-5 text-lgs-red" />
              Intervention Caseload
            </h2>
            <p className="text-sm text-slate-500 mt-1">Distribution of students across support tiers.</p>
          </div>
          <div className="flex-1 min-h-[200px]">
            <ResponsiveContainer width="100%" height="100%">
              <PieChart>
                <Pie data={donutData} cx="50%" cy="50%" innerRadius={60} outerRadius={80} paddingAngle={2} dataKey="value" stroke="none">
                  {donutData.map((entry, i) => <Cell key={i} fill={entry.color} />)}
                </Pie>
                <RechartsTooltip formatter={(v: number, name: string, p: any) => [`${v}% (${p.payload.count} students)`, name]} contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} />
              </PieChart>
            </ResponsiveContainer>
          </div>
          <div className="space-y-3 mt-4">
            {donutData.map(item => (
              <div key={item.name} className="flex items-center justify-between text-sm font-bold">
                <div className="flex items-center gap-2 text-lgs-blue">
                  <div className="w-3 h-3 rounded-full" style={{ backgroundColor: item.color }} />
                  {item.name}
                </div>
                <div className="text-lgs-blue">{item.value}% <span className="text-slate-400 font-normal ml-1">({item.count})</span></div>
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Grade-level breakdown */}
      {stats.gradeData.length > 0 && (
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
              <BarChart data={stats.gradeData} layout="vertical" margin={{ top: 0, right: 0, left: 0, bottom: 0 }} barSize={24}>
                <XAxis type="number" hide />
                <YAxis dataKey="grade" type="category" axisLine={false} tickLine={false} tick={{ fill: '#214965', fontSize: 12, fontWeight: 600 }} width={80} />
                <RechartsTooltip formatter={(v: number) => [`${v}%`, '']} contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} />
                <Legend iconType="square" wrapperStyle={{ fontSize: '12px', paddingTop: '10px' }} />
                <Bar dataKey="proficient" name="Proficient" stackId="a" fill="#214965" radius={[4, 0, 0, 4]} />
                <Bar dataKey="developing" name="Developing" stackId="a" fill="#9ca3af" />
                <Bar dataKey="critical" name="Critical Concern" stackId="a" fill="#b91c1c" radius={[0, 4, 4, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      )}

      {/* Grade → Teacher → Student drill-down */}
      <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
        <div className="mb-4 flex items-center gap-3">
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

        {loadingDrill ? (
          <div className="text-slate-400 text-sm py-8 text-center">Loading…</div>
        ) : drillView.level === 'grades' ? (
          gradeRows.length === 0 ? (
            <div className="text-slate-400 text-sm py-8 text-center">No tiered students yet.</div>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-xs font-bold text-slate-400 uppercase tracking-wider border-b border-slate-200">
                  <th className="pb-3 pr-4">Grade</th>
                  <th className="pb-3 pr-4 text-lgs-blue">Tier 1</th>
                  <th className="pb-3 pr-4 text-slate-500">Tier 2</th>
                  <th className="pb-3 pr-4 text-lgs-red">Tier 3</th>
                  <th className="pb-3 pr-4">Total</th>
                  <th className="pb-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {gradeRows.map(row => (
                  <tr key={row.grade} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => drillToTeachers(row.grade)}>
                    <td className="py-3 pr-4 font-semibold text-lgs-blue">Grade {row.grade}</td>
                    <td className="py-3 pr-4">{row.tier1}</td>
                    <td className="py-3 pr-4">{row.tier2}</td>
                    <td className="py-3 pr-4">{row.tier3}</td>
                    <td className="py-3 pr-4">{row.total}</td>
                    <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        ) : drillView.level === 'teachers' ? (
          teacherRows.length === 0 ? (
            <div className="text-slate-400 text-sm py-8 text-center">No teachers found for this grade.</div>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-xs font-bold text-slate-400 uppercase tracking-wider border-b border-slate-200">
                  <th className="pb-3 pr-4">Teacher / Class</th>
                  <th className="pb-3 pr-4 text-lgs-blue">Tier 1</th>
                  <th className="pb-3 pr-4 text-slate-500">Tier 2</th>
                  <th className="pb-3 pr-4 text-lgs-red">Tier 3</th>
                  <th className="pb-3 pr-4">Total</th>
                  <th className="pb-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {teacherRows.map(row => (
                  <tr key={row.teacher} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => drillToStudents((drillView as any).grade, row.teacher)}>
                    <td className="py-3 pr-4 font-semibold text-lgs-blue">{row.teacher}</td>
                    <td className="py-3 pr-4">{row.tier1}</td>
                    <td className="py-3 pr-4">{row.tier2}</td>
                    <td className="py-3 pr-4">{row.tier3}</td>
                    <td className="py-3 pr-4">{row.total}</td>
                    <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                  </tr>
                ))}
                <tr className="hover:bg-slate-100 transition-colors cursor-pointer text-slate-400" onClick={() => drillToStudents((drillView as any).grade)}>
                  <td className="py-3 pr-4 italic">View all students in grade</td>
                  <td colSpan={4} />
                  <td className="py-3 text-slate-300"><ChevronRight className="w-4 h-4" /></td>
                </tr>
              </tbody>
            </table>
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
                {drillStudents.map(s => (
                  <tr key={s.studentId} className="hover:bg-slate-100 transition-colors cursor-pointer" onClick={() => navigate(`/students/${s.studentId}`)}>
                    <td className="py-3 pr-4 font-semibold text-lgs-blue">{s.fullName}</td>
                    <td className="py-3 pr-4 text-slate-500">{s.classGroup || '—'}</td>
                    <td className="py-3 pr-4">
                      <span className={`text-xs font-bold px-2 py-0.5 rounded-full ${tierColor[s.tier] ?? 'text-slate-400 bg-slate-100'}`}>{s.tier}</span>
                    </td>
                    <td className="py-3 text-slate-500 text-xs">{s.tierStatus}</td>
                  </tr>
                ))}
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
                Caseload by Class Group
              </h2>
              <p className="text-sm text-slate-500 mt-1">Distribution of student tiers across class groups.</p>
            </div>
            <div className="h-[400px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={stats.homeRoomData} margin={{ top: 20, right: 30, left: 20, bottom: 60 }}>
                  <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                  <XAxis dataKey="initials" axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} interval={0} />
                  <YAxis axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} />
                  <RechartsTooltip contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }} cursor={{ fill: '#f1f5f9' }} />
                  <Legend wrapperStyle={{ paddingTop: '20px' }} />
                  <Bar dataKey="Tier 1" stackId="a" fill="#214965" />
                  <Bar dataKey="Tier 2" stackId="a" fill="#9ca3af" />
                  <Bar dataKey="Tier 3" stackId="a" fill="#b91c1c" />
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
            <p className="text-sm text-slate-500 mt-1">Student tier distribution by location.</p>
          </div>
          <div className="h-[400px] relative rounded-xl overflow-hidden border border-slate-200 z-0">
            <MapContainer center={defaultCenter} zoom={10} style={{ height: '100%', width: '100%' }}>
              <TileLayer
                attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
                url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
              />
            </MapContainer>
          </div>
          <p className="text-xs text-slate-400 mt-2 italic">Geographic data not available for this deployment.</p>
        </div>
      </div>
    </div>
  );
}

function KpiCard({ icon, iconBg, badge, badgeColor, label, value, sub, tooltip }: {
  icon: React.ReactNode;
  iconBg: string;
  badge: string;
  badgeColor: string;
  label: string;
  value: string;
  sub: string;
  tooltip: string;
}) {
  return (
    <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
      <div className="flex justify-between items-start mb-4">
        <div className={`w-12 h-12 rounded-xl ${iconBg} flex items-center justify-center`}>{icon}</div>
        <span className={`text-xs font-bold flex items-center gap-1 px-2 py-1 rounded-md ${badgeColor}`}>{badge}</span>
      </div>
      <div className="flex items-center gap-1 mb-1 relative group">
        <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">{label}</h3>
        <Info className="w-3.5 h-3.5 text-slate-400 cursor-help" />
        <div className="absolute bottom-full left-0 mb-2 hidden group-hover:block w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none normal-case tracking-normal">
          {tooltip}
          <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800" />
        </div>
      </div>
      <div className="text-3xl font-black text-lgs-blue mb-1">{value}</div>
      <p className="text-xs text-slate-500 font-medium">{sub}</p>
    </div>
  );
}
