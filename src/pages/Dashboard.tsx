import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { Search, AlertCircle, TrendingUp, Award, Users, Target, Download, Info } from 'lucide-react';
import { collection, query, where, getDocs } from 'firebase/firestore';
import { db } from '../firebase';
import { handleFirestoreError, OperationType } from '../lib/utils';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer,
  PieChart, Pie, Cell,
  BarChart, Bar
} from 'recharts';

export default function Dashboard() {
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [error, setError] = useState('');
  const navigate = useNavigate();

  const [stats, setStats] = useState({
    tier1: 65,
    tier2: 25,
    tier3: 10,
    totalStudents: 182,
    gradeData: [
      { grade: 'Grade 1', proficient: 60, developing: 25, critical: 15 },
      { grade: 'Grade 2', proficient: 55, developing: 30, critical: 15 },
      { grade: 'Grade 3', proficient: 50, developing: 35, critical: 15 },
      { grade: 'Grade 4', proficient: 58, developing: 27, critical: 15 },
      { grade: 'Grade 5', proficient: 52, developing: 33, critical: 15 },
      { grade: 'Grade 6', proficient: 48, developing: 37, critical: 15 },
    ]
  });

  useEffect(() => {
    const fetchStats = async () => {
      try {
        const studentsSnap = await getDocs(collection(db, 'students'));
        if (!studentsSnap.empty) {
          let t1 = 0, t2 = 0, t3 = 0;
          const gradeMap: Record<string, { proficient: number, developing: number, critical: number }> = {};
          
          studentsSnap.docs.forEach(doc => {
            const data = doc.data();
            const tier = data.tier || 'Pending';
            let grade = data.grade || 'Unknown';
            
            // Normalize grade formatting
            if (grade !== 'Unknown') {
              grade = String(grade).replace(/^0+(?=\d)/, ''); // Remove leading zeros
              grade = `Grade ${grade}`;
            }
            
            if (tier === 'Tier 1') t1++;
            else if (tier === 'Tier 2') t2++;
            else if (tier === 'Tier 3') t3++;

            if (grade !== 'Grade Unknown' && tier !== 'Pending') {
              if (!gradeMap[grade]) gradeMap[grade] = { proficient: 0, developing: 0, critical: 0 };
              if (tier === 'Tier 1') gradeMap[grade].proficient++;
              else if (tier === 'Tier 2') gradeMap[grade].developing++;
              else if (tier === 'Tier 3') gradeMap[grade].critical++;
            }
          });

          const total = t1 + t2 + t3;
          if (total > 0) {
            const gradeData = Object.keys(gradeMap).sort((a, b) => {
              const numA = parseInt(a.replace(/\D/g, '')) || 0;
              const numB = parseInt(b.replace(/\D/g, '')) || 0;
              return numA - numB;
            }).map(k => {
              const totalInGrade = gradeMap[k].proficient + gradeMap[k].developing + gradeMap[k].critical;
              return {
                grade: k,
                proficient: Math.round((gradeMap[k].proficient / totalInGrade) * 100),
                developing: Math.round((gradeMap[k].developing / totalInGrade) * 100),
                critical: Math.round((gradeMap[k].critical / totalInGrade) * 100),
              };
            });
            
            setStats({
              tier1: Math.round((t1 / total) * 100),
              tier2: Math.round((t2 / total) * 100),
              tier3: Math.round((t3 / total) * 100),
              totalStudents: total,
              gradeData: gradeData.length > 0 ? gradeData : stats.gradeData
            });
          }
        }
      } catch (err) {
        console.error("Error fetching stats", err);
      }
    };
    fetchStats();
  }, []);

  const handleSearch = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!searchQuery.trim()) return;

    setIsSearching(true);
    setError('');

    try {
      const q = query(collection(db, 'students'), where('stn', '==', searchQuery.trim()));
      const querySnapshot = await getDocs(q);

      if (querySnapshot.empty) {
        setError('No student found with that STN.');
      } else {
        navigate(`/students/${searchQuery.trim()}`);
      }
    } catch (err) {
      handleFirestoreError(err, OperationType.GET, 'students');
      setError('An error occurred while searching.');
    } finally {
      setIsSearching(false);
    }
  };

  const timelineData = [
    { month: 'Sep', ela: 42, math: 38 },
    { month: 'Oct', ela: 45, math: 42 },
    { month: 'Nov', ela: 48, math: 44 },
    { month: 'Dec', ela: 46, math: 41 },
    { month: 'Jan', ela: 52, math: 50 },
    { month: 'Feb', ela: 58, math: 54 },
    { month: 'Mar', ela: 61, math: 59 },
  ];

  const donutData = [
    { name: 'Tier 1', value: stats.tier1, color: '#214965' },
    { name: 'Tier 2', value: stats.tier2, color: '#9ca3af' },
    { name: 'Tier 3', value: stats.tier3, color: '#b91c1c' },
  ];

  return (
    <div className="space-y-8 max-w-7xl mx-auto pb-12">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-lgs-blue">Performance Trends</h1>
          <p className="text-slate-500 mt-1">Institutional academic growth and intervention analytics for the 2024-2025 school year.</p>
        </div>
        <button className="inline-flex items-center justify-center gap-2 px-4 py-2 bg-lgs-blue text-white rounded-lg font-medium hover:bg-lgs-blue-dark transition-colors">
          <Download className="w-4 h-4" />
          Export Report
        </button>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6">
        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-slate-100 flex items-center justify-center">
              <TrendingUp className="w-6 h-6 text-lgs-blue" />
            </div>
            <span className="text-green-600 text-xs font-bold flex items-center gap-1 bg-green-50 px-2 py-1 rounded-md">
              ↗ IMPROVED
            </span>
          </div>
          <div className="flex items-center gap-1 mb-1 relative group">
            <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">Avg. ELA Growth</h3>
            <Info className="w-3.5 h-3.5 text-slate-400 cursor-help" />
            <div className="absolute bottom-full left-0 mb-2 hidden group-hover:block w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none">
              <div className="space-y-1.5">
                <p><strong className="text-slate-300">Meaning:</strong> Average increase in ELA scores across the student body.</p>
                <p><strong className="text-slate-300">Source:</strong> ILEARN Checkpoint & IXL.</p>
                <p><strong className="text-slate-300">Calculation:</strong> (Current Avg - Previous Avg) / Previous Avg.</p>
              </div>
              <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800"></div>
            </div>
          </div>
          <div className="text-3xl font-black text-lgs-blue mb-1">+19%</div>
          <p className="text-xs text-slate-500 font-medium">vs last semester</p>
        </div>

        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-red-50 flex items-center justify-center">
              <Award className="w-6 h-6 text-lgs-red" />
            </div>
            <span className="text-green-600 text-xs font-bold flex items-center gap-1 bg-green-50 px-2 py-1 rounded-md">
              ↗ IMPROVED
            </span>
          </div>
          <div className="flex items-center gap-1 mb-1 relative group">
            <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">Math Proficiency</h3>
            <Info className="w-3.5 h-3.5 text-slate-400 cursor-help" />
            <div className="absolute bottom-full left-0 mb-2 hidden group-hover:block w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none">
              <div className="space-y-1.5">
                <p><strong className="text-slate-300">Meaning:</strong> Percentage of students meeting grade-level expectations.</p>
                <p><strong className="text-slate-300">Source:</strong> ILEARN Checkpoint & State Assessments.</p>
                <p><strong className="text-slate-300">Calculation:</strong> (Proficient Students / Total Students) × 100.</p>
              </div>
              <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800"></div>
            </div>
          </div>
          <div className="text-3xl font-black text-lgs-blue mb-1">64.2%</div>
          <p className="text-xs text-slate-500 font-medium">+4.1% school-wide</p>
        </div>

        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-slate-100 flex items-center justify-center">
              <Users className="w-6 h-6 text-lgs-blue" />
            </div>
            <span className="text-lgs-red text-xs font-bold flex items-center gap-1 bg-red-50 px-2 py-1 rounded-md">
              ↘ ALERT
            </span>
          </div>
          <div className="flex items-center gap-1 mb-1 relative group">
            <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">Active Caseload</h3>
            <Info className="w-3.5 h-3.5 text-slate-400 cursor-help" />
            <div className="absolute bottom-full left-0 mb-2 hidden group-hover:block w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none">
              <div className="space-y-1.5">
                <p><strong className="text-slate-300">Meaning:</strong> Total number of students currently tracked in the system.</p>
                <p><strong className="text-slate-300">Source:</strong> PowerSchool Demographics.</p>
                <p><strong className="text-slate-300">Calculation:</strong> Count of all active student records.</p>
              </div>
              <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800"></div>
            </div>
          </div>
          <div className="text-3xl font-black text-lgs-blue mb-1">{stats.totalStudents}</div>
          <p className="text-xs text-slate-500 font-medium">-{Math.round(stats.totalStudents * (stats.tier3 / 100))} students in Tier 3</p>
        </div>

        <div className="bg-white p-6 rounded-2xl shadow-sm border border-slate-100 flex flex-col">
          <div className="flex justify-between items-start mb-4">
            <div className="w-12 h-12 rounded-xl bg-slate-100 flex items-center justify-center">
              <Target className="w-6 h-6 text-lgs-blue" />
            </div>
            <span className="text-green-600 text-xs font-bold flex items-center gap-1 bg-green-50 px-2 py-1 rounded-md">
              ↗ IMPROVED
            </span>
          </div>
          <div className="flex items-center gap-1 mb-1 relative group">
            <h3 className="text-xs font-bold text-slate-500 uppercase tracking-wider">Target Goal</h3>
            <Info className="w-3.5 h-3.5 text-slate-400 cursor-help" />
            <div className="absolute bottom-full right-0 mb-2 hidden group-hover:block w-64 p-3 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none">
              <div className="space-y-1.5">
                <p><strong className="text-slate-300">Meaning:</strong> Institutional objective for overall student proficiency.</p>
                <p><strong className="text-slate-300">Source:</strong> District Strategic Plan.</p>
                <p><strong className="text-slate-300">Calculation:</strong> Statically defined target set by administration.</p>
              </div>
              <div className="absolute top-full right-4 border-4 border-transparent border-t-slate-800"></div>
            </div>
          </div>
          <div className="text-3xl font-black text-lgs-blue mb-1">85%</div>
          <p className="text-xs text-slate-500 font-medium">Proficiency objective</p>
        </div>
      </div>

      {/* Charts Row 1 */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Line Chart */}
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
                <Tooltip 
                  contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                  itemStyle={{ fontSize: '12px', fontWeight: 500 }}
                  labelStyle={{ fontSize: '12px', color: '#64748b', marginBottom: '4px' }}
                />
                <Legend iconType="square" wrapperStyle={{ fontSize: '12px', paddingTop: '20px' }} />
                <Line type="monotone" name="ELA Growth" dataKey="ela" stroke="#214965" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
                <Line type="monotone" name="Math Growth" dataKey="math" stroke="#b91c1c" strokeWidth={3} dot={{ r: 4, strokeWidth: 2 }} activeDot={{ r: 6 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </div>

        {/* Donut Chart */}
        <div className="bg-[#fff5f5] p-6 rounded-2xl border border-red-50 flex flex-col">
          <div className="mb-2">
            <div className="flex items-center gap-2 relative group">
              <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
                <Users className="w-5 h-5 text-lgs-red" />
                Intervention Caseload
              </h2>
              <Info className="w-4 h-4 text-slate-400 cursor-help" />
              <div className="absolute bottom-full left-0 mb-2 hidden group-hover:block w-80 p-4 bg-slate-800 text-white text-xs rounded-lg shadow-xl z-50 pointer-events-none normal-case tracking-normal">
                <h4 className="font-bold text-sm mb-2 text-slate-100">Tiering Criteria</h4>
                <div className="space-y-2">
                  <p><strong className="text-green-400">Tier 1:</strong> On/Above grade level in BOTH Math and ELA.</p>
                  <p><strong className="text-yellow-400">Tier 2:</strong> On/Above grade level in ONE subject, Below in the other.</p>
                  <p><strong className="text-red-400">Tier 3:</strong> Below grade level in BOTH Math and ELA.</p>
                  <div className="border-t border-slate-600 pt-2 mt-2">
                    <p><strong className="text-slate-300">Calculation:</strong> Evaluates the most recent assessment for each subject. "On/Above" requires scoring &ge; 40th percentile or achieving a "Proficient"/"Meets" status.</p>
                  </div>
                </div>
                <div className="absolute top-full left-4 border-4 border-transparent border-t-slate-800"></div>
              </div>
            </div>
            <p className="text-sm text-slate-500 mt-1">Distribution of students across support tiers.</p>
          </div>
          <div className="flex-1 min-h-[200px] relative">
            <ResponsiveContainer width="100%" height="100%">
              <PieChart>
                <Pie
                  data={donutData}
                  cx="50%"
                  cy="50%"
                  innerRadius={60}
                  outerRadius={80}
                  paddingAngle={2}
                  dataKey="value"
                  stroke="none"
                >
                  {donutData.map((entry, index) => (
                    <Cell key={`cell-${index}`} fill={entry.color} />
                  ))}
                </Pie>
                <Tooltip 
                  formatter={(value: number) => [`${value}%`, '']}
                  contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                />
              </PieChart>
            </ResponsiveContainer>
          </div>
          <div className="space-y-3 mt-4">
            {donutData.map((item) => (
              <div key={item.name} className="flex items-center justify-between text-sm font-bold">
                <div className="flex items-center gap-2 text-lgs-blue">
                  <div className="w-3 h-3 rounded-full" style={{ backgroundColor: item.color }}></div>
                  {item.name}
                </div>
                <div className="text-lgs-blue">{item.value}%</div>
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Charts Row 2 */}
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
            <BarChart
              data={stats.gradeData}
              layout="vertical"
              margin={{ top: 0, right: 0, left: 0, bottom: 0 }}
              barSize={24}
            >
              <XAxis type="number" hide />
              <YAxis dataKey="grade" type="category" axisLine={false} tickLine={false} tick={{ fill: '#214965', fontSize: 12, fontWeight: 600 }} width={80} />
              <Tooltip 
                formatter={(value: number) => [`${value}%`, '']}
                contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
              />
              <Legend iconType="square" wrapperStyle={{ fontSize: '12px', paddingTop: '10px' }} />
              <Bar dataKey="proficient" name="Proficient" stackId="a" fill="#214965" radius={[4, 0, 0, 4]} />
              <Bar dataKey="developing" name="Developing" stackId="a" fill="#9ca3af" />
              <Bar dataKey="critical" name="Critical Concern" stackId="a" fill="#b91c1c" radius={[0, 4, 4, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>

    </div>
  );
}
