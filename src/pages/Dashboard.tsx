import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { TrendingUp, Award, Users, Target, Download, Info } from 'lucide-react';
import { studentsApi, Student, PagedResult } from '../lib/api';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, Legend, ResponsiveContainer,
  PieChart, Pie, Cell,
  BarChart, Bar
} from 'recharts';
import { MapContainer, TileLayer, CircleMarker, Popup } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

const defaultCenter: [number, number] = [39.7684, -86.1581];

const timelineData = [
  { month: 'Sep', ela: 42, math: 38 },
  { month: 'Oct', ela: 45, math: 42 },
  { month: 'Nov', ela: 48, math: 44 },
  { month: 'Dec', ela: 46, math: 41 },
  { month: 'Jan', ela: 52, math: 50 },
  { month: 'Feb', ela: 58, math: 54 },
  { month: 'Mar', ela: 61, math: 59 },
];

interface Stats {
  tier1Pct: number;
  tier2Pct: number;
  tier3Pct: number;
  tier1Count: number;
  tier2Count: number;
  tier3Count: number;
  totalStudents: number;
  gradeData: { grade: string; proficient: number; developing: number; critical: number }[];
  homeRoomData: { homeRoom: string; initials: string; 'Tier 1': number; 'Tier 2': number; 'Tier 3': number; total: number }[];
}

function buildStats(students: Student[]): Stats {
  let t1 = 0, t2 = 0, t3 = 0;
  const gradeMap: Record<string, { proficient: number; developing: number; critical: number }> = {};
  const homeRoomMap: Record<string, { tier1: number; tier2: number; tier3: number }> = {};

  for (const s of students) {
    const tier = s.tier || 'Pending';
    const grade = s.grade ? `Grade ${String(s.grade).replace(/^0+(?=\d)/, '')}` : 'Unknown';
    const homeRoom = s.classGroup || 'Unassigned';

    if (tier === 'Tier 1') t1++;
    else if (tier === 'Tier 2') t2++;
    else if (tier === 'Tier 3') t3++;

    if (tier !== 'Pending') {
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
    gradeData: gradeData.length ? gradeData : [
      { grade: 'Grade 1', proficient: 60, developing: 25, critical: 15 },
      { grade: 'Grade 2', proficient: 55, developing: 30, critical: 15 },
      { grade: 'Grade 3', proficient: 50, developing: 35, critical: 15 },
    ],
    homeRoomData,
  };
}

export default function Dashboard() {
  const navigate = useNavigate();
  const [stats, setStats] = useState<Stats>({
    tier1Pct: 65, tier2Pct: 25, tier3Pct: 10,
    tier1Count: 0, tier2Count: 0, tier3Count: 0,
    totalStudents: 0,
    gradeData: [],
    homeRoomData: [],
  });
  const [loadingStats, setLoadingStats] = useState(true);

  useEffect(() => {
    async function fetchAll() {
      setLoadingStats(true);
      try {
        // Fetch first page to get total, then all records
        const first = await studentsApi.list({ page: 1, pageSize: 500 });
        const allStudents = first.items;

        // If there are more pages, fetch them
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
    fetchAll();
  }, []);

  const donutData = [
    { name: 'Tier 1', value: stats.tier1Pct, count: stats.tier1Count, color: '#214965' },
    { name: 'Tier 2', value: stats.tier2Pct, count: stats.tier2Count, color: '#9ca3af' },
    { name: 'Tier 3', value: stats.tier3Pct, count: stats.tier3Count, color: '#b91c1c' },
  ];

  return (
    <div className="space-y-8 max-w-7xl mx-auto pb-12">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-lgs-blue">Performance Trends</h1>
          <p className="text-slate-500 mt-1">Institutional academic growth and intervention analytics for the 2024-2025 school year.</p>
        </div>
        <button
          onClick={() => navigate('/data')}
          className="inline-flex items-center justify-center gap-2 px-4 py-2 bg-lgs-blue text-white rounded-lg font-medium hover:bg-lgs-blue-dark transition-colors"
        >
          <Download className="w-4 h-4" />
          Export Report
        </button>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6">
        <KpiCard icon={<TrendingUp className="w-6 h-6 text-lgs-blue" />} iconBg="bg-slate-100" badge="↗ IMPROVED" badgeColor="text-green-600 bg-green-50" label="Avg. ELA Growth" value="+19%" sub="vs last semester" tooltip="Average increase in ELA scores across the student body. Source: ILEARN Checkpoint & IXL." />
        <KpiCard icon={<Award className="w-6 h-6 text-lgs-red" />} iconBg="bg-red-50" badge="↗ IMPROVED" badgeColor="text-green-600 bg-green-50" label="Math Proficiency" value="64.2%" sub="+4.1% school-wide" tooltip="Percentage of students meeting grade-level expectations in Math." />
        <KpiCard icon={<Users className="w-6 h-6 text-lgs-blue" />} iconBg="bg-slate-100" badge="↘ ALERT" badgeColor="text-lgs-red bg-red-50" label="Active Caseload" value={loadingStats ? '...' : String(stats.totalStudents)} sub={loadingStats ? '' : `${stats.tier3Count} students in Tier 3`} tooltip="Total number of students currently tracked in the system." />
        <KpiCard icon={<Target className="w-6 h-6 text-lgs-blue" />} iconBg="bg-slate-100" badge="↗ IMPROVED" badgeColor="text-green-600 bg-green-50" label="Target Goal" value="85%" sub="Proficiency objective" tooltip="Institutional objective for overall student proficiency (District Strategic Plan)." />
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
