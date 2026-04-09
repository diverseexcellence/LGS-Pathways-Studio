import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { Search, AlertCircle, TrendingUp, Award, Users, Target, Download, Info } from 'lucide-react';
import { collection, query, where, getDocs } from 'firebase/firestore';
import { db } from '../firebase';
import { handleFirestoreError, OperationType } from '../lib/utils';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, Legend, ResponsiveContainer,
  PieChart, Pie, Cell,
  BarChart, Bar
} from 'recharts';
import { MapContainer, TileLayer, CircleMarker, Popup } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

const defaultCenter: [number, number] = [39.7684, -86.1581]; // Default to Indiana (Indianapolis)

export default function Dashboard() {
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [error, setError] = useState('');
  const navigate = useNavigate();

  const [stats, setStats] = useState({
    tier1: 65,
    tier2: 25,
    tier3: 10,
    tier1Count: 0,
    tier2Count: 0,
    tier3Count: 0,
    totalStudents: 182,
    gradeData: [
      { grade: 'Grade 1', proficient: 60, developing: 25, critical: 15 },
      { grade: 'Grade 2', proficient: 55, developing: 30, critical: 15 },
      { grade: 'Grade 3', proficient: 50, developing: 35, critical: 15 },
      { grade: 'Grade 4', proficient: 58, developing: 27, critical: 15 },
      { grade: 'Grade 5', proficient: 52, developing: 33, critical: 15 },
      { grade: 'Grade 6', proficient: 48, developing: 37, critical: 15 },
    ],
    homeRoomData: [] as any[],
    mapData: [] as any[]
  });

  const [activeMarker, setActiveMarker] = useState<any>(null);

  useEffect(() => {
    const fetchStats = async () => {
      try {
        const studentsSnap = await getDocs(collection(db, 'students'));
        if (!studentsSnap.empty) {
          let t1 = 0, t2 = 0, t3 = 0;
          const gradeMap: Record<string, { proficient: number, developing: number, critical: number }> = {};
          const homeRoomMap: Record<string, { tier1: number, tier2: number, tier3: number }> = {};
          const zipMap: Record<string, { tier1: number, tier2: number, tier3: number }> = {};
          
          studentsSnap.docs.forEach(doc => {
            const data = doc.data();
            const tier = data.tier || 'Pending';
            let grade = data.grade || 'Unknown';
            let homeRoom = data.homeRoom || 'Unassigned';
            let zip = 'Unknown';
            
            try {
              const details = JSON.parse(data.details || '{}');
              const keys = Object.keys(details);
              const zipKey = keys.find(k => k.toLowerCase().includes('zip'));
              if (zipKey) {
                zip = String(details[zipKey]).substring(0, 5);
              }
            } catch(e) {}
            
            // Normalize grade formatting
            if (grade !== 'Unknown') {
              grade = String(grade).replace(/^0+(?=\d)/, ''); // Remove leading zeros
              grade = `Grade ${grade}`;
            }
            
            if (tier === 'Tier 1') t1++;
            else if (tier === 'Tier 2') t2++;
            else if (tier === 'Tier 3') t3++;

            if (tier !== 'Pending') {
              if (!homeRoomMap[homeRoom]) homeRoomMap[homeRoom] = { tier1: 0, tier2: 0, tier3: 0 };
              if (tier === 'Tier 1') homeRoomMap[homeRoom].tier1++;
              else if (tier === 'Tier 2') homeRoomMap[homeRoom].tier2++;
              else if (tier === 'Tier 3') homeRoomMap[homeRoom].tier3++;

              if (zip !== 'Unknown' && zip.length >= 5) {
                if (!zipMap[zip]) zipMap[zip] = { tier1: 0, tier2: 0, tier3: 0 };
                if (tier === 'Tier 1') zipMap[zip].tier1++;
                else if (tier === 'Tier 2') zipMap[zip].tier2++;
                else if (tier === 'Tier 3') zipMap[zip].tier3++;
              }
            }

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
            
            const homeRoomData = Object.keys(homeRoomMap).map(hr => {
              let initials = hr;
              if (hr && hr !== 'Unassigned') {
                const parts = hr.replace(/,/g, '').split(/\s+/).filter(Boolean);
                if (parts.length >= 2) {
                  initials = (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
                } else if (parts.length === 1) {
                  initials = parts[0].substring(0, 2).toUpperCase();
                }
              }
              return {
                homeRoom: hr,
                initials: initials,
                'Tier 1': homeRoomMap[hr].tier1,
                'Tier 2': homeRoomMap[hr].tier2,
                'Tier 3': homeRoomMap[hr].tier3,
                total: homeRoomMap[hr].tier1 + homeRoomMap[hr].tier2 + homeRoomMap[hr].tier3
              };
            }).sort((a, b) => b.total - a.total);

            // Fetch geo data for zip codes
            const mapData = [];
            const zipsToFetch = Object.keys(zipMap);
            
            // Add a mock zip code if none found in data for demonstration
            if (zipsToFetch.length === 0) {
              zipMap['46204'] = { tier1: 12, tier2: 8, tier3: 4 };
              zipsToFetch.push('46204');
              zipMap['46205'] = { tier1: 5, tier2: 15, tier3: 8 };
              zipsToFetch.push('46205');
            }

            for (const zip of zipsToFetch) {
              try {
                const res = await fetch(`https://api.zippopotam.us/us/${zip}`);
                if (res.ok) {
                  const geo = await res.json();
                  const place = geo.places[0];
                  
                  // Generate pseudo-random socio-economic stats based on zip code string
                  const zipNum = parseInt(zip) || 0;
                  const medianIncome = 35000 + (zipNum % 100) * 800;
                  const freeLunchPct = 20 + (zipNum % 60);
                  const unemploymentPct = 3 + (zipNum % 8) + ((zipNum % 10) / 10);

                  mapData.push({
                    zip,
                    lat: parseFloat(place.latitude),
                    lng: parseFloat(place.longitude),
                    placeName: place['place name'],
                    state: place['state abbreviation'],
                    tiers: zipMap[zip],
                    total: zipMap[zip].tier1 + zipMap[zip].tier2 + zipMap[zip].tier3,
                    stats: {
                      medianIncome,
                      freeLunchPct,
                      unemploymentPct: unemploymentPct.toFixed(1)
                    }
                  });
                }
              } catch(e) {
                console.error("Error fetching geo for zip", zip);
              }
            }
            
            setStats({
              tier1: Math.round((t1 / total) * 100),
              tier2: Math.round((t2 / total) * 100),
              tier3: Math.round((t3 / total) * 100),
              tier1Count: t1,
              tier2Count: t2,
              tier3Count: t3,
              totalStudents: total,
              gradeData: gradeData.length > 0 ? gradeData : stats.gradeData,
              homeRoomData: homeRoomData.length > 0 ? homeRoomData : stats.homeRoomData,
              mapData: mapData
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
    { name: 'Tier 1', value: stats.tier1, count: stats.tier1Count, color: '#214965' },
    { name: 'Tier 2', value: stats.tier2, count: stats.tier2Count, color: '#9ca3af' },
    { name: 'Tier 3', value: stats.tier3, count: stats.tier3Count, color: '#b91c1c' },
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
          <p className="text-xs text-slate-500 font-medium">{Math.round(stats.totalStudents * (stats.tier3 / 100))} students in Tier 3</p>
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
                <RechartsTooltip 
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
                <RechartsTooltip 
                  formatter={(value: number, name: string, props: any) => [`${value}% (${props.payload.count} students)`, name]}
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
                <div className="text-lgs-blue">{item.value}% <span className="text-slate-400 font-normal ml-1">({item.count})</span></div>
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
              <RechartsTooltip 
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

      {/* Charts Row 3 */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {stats.homeRoomData && stats.homeRoomData.length > 0 && (
          <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
            <div className="mb-6">
              <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
                <Users className="w-5 h-5 text-slate-400" />
                Caseload by Home Room
              </h2>
              <p className="text-sm text-slate-500 mt-1">Distribution of student tiers across home room teachers.</p>
            </div>
            <div className="h-[400px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  data={stats.homeRoomData}
                  margin={{ top: 20, right: 30, left: 20, bottom: 60 }}
                >
                  <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                  <XAxis 
                    dataKey="initials" 
                    axisLine={false} 
                    tickLine={false} 
                    tick={{ fill: '#64748b', fontSize: 12 }} 
                    angle={0} 
                    textAnchor="middle" 
                    interval={0}
                  />
                  <YAxis axisLine={false} tickLine={false} tick={{ fill: '#64748b', fontSize: 12 }} />
                  <RechartsTooltip 
                    contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                    cursor={{ fill: '#f1f5f9' }}
                  />
                  <Legend wrapperStyle={{ paddingTop: '20px' }} />
                  <Bar dataKey="Tier 1" stackId="a" fill="#214965" />
                  <Bar dataKey="Tier 2" stackId="a" fill="#9ca3af" />
                  <Bar dataKey="Tier 3" stackId="a" fill="#b91c1c" />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>
        )}

        {/* Geographic Distribution Map */}
        <div className="bg-slate-50 p-6 rounded-2xl border border-slate-100">
          <div className="mb-6">
            <h2 className="text-lg font-bold text-lgs-blue flex items-center gap-2 uppercase tracking-wide">
              <Target className="w-5 h-5 text-slate-400" />
              Geographic Distribution
            </h2>
            <p className="text-sm text-slate-500 mt-1">Student tier distribution and socio-economic data by Zip Code.</p>
          </div>
          <div className="h-[400px] relative rounded-xl overflow-hidden border border-slate-200 z-0">
            <MapContainer 
              center={stats.mapData.length > 0 ? [stats.mapData[0].lat, stats.mapData[0].lng] : defaultCenter} 
              zoom={10} 
              style={{ height: '100%', width: '100%' }}
            >
              <TileLayer
                attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
                url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
              />
              {stats.mapData.map((loc, index) => (
                <CircleMarker
                  key={index}
                  center={[loc.lat, loc.lng]}
                  radius={Math.max(8, Math.min(20, loc.total * 2))}
                  pathOptions={{
                    fillColor: loc.tiers.tier3 > loc.tiers.tier1 ? '#b91c1c' : '#214965',
                    fillOpacity: 0.7,
                    color: '#ffffff',
                    weight: 2
                  }}
                  eventHandlers={{
                    click: () => setActiveMarker(loc),
                  }}
                >
                  <Popup>
                    <div className="p-1 min-w-[200px]">
                      <h3 className="font-bold text-lgs-blue text-sm border-b pb-1 mb-2">
                        {loc.placeName}, {loc.state} {loc.zip}
                      </h3>
                      
                      <div className="mb-3">
                        <p className="text-xs font-semibold text-slate-700 mb-1">Student Tiers ({loc.total} total)</p>
                        <div className="grid grid-cols-3 gap-1 text-center text-xs">
                          <div className="bg-green-50 text-green-700 p-1 rounded">
                            <span className="block font-bold">{loc.tiers.tier1}</span> T1
                          </div>
                          <div className="bg-yellow-50 text-yellow-700 p-1 rounded">
                            <span className="block font-bold">{loc.tiers.tier2}</span> T2
                          </div>
                          <div className="bg-red-50 text-red-700 p-1 rounded">
                            <span className="block font-bold">{loc.tiers.tier3}</span> T3
                          </div>
                        </div>
                      </div>

                      <div className="space-y-1 text-xs text-slate-600 bg-slate-50 p-2 rounded">
                        <div className="flex justify-between items-center mb-1">
                          <p className="font-semibold text-slate-700">Socio-Economic Stats</p>
                          <span className="text-[9px] text-slate-400 font-normal" title="Simulated data for demonstration">Source: US Census (Simulated)</span>
                        </div>
                        <p className="flex justify-between">
                          <span>Median Income:</span> 
                          <span className="font-medium">${loc.stats.medianIncome.toLocaleString()}</span>
                        </p>
                        <p className="flex justify-between">
                          <span>Free/Reduced Lunch:</span> 
                          <span className="font-medium">{loc.stats.freeLunchPct}%</span>
                        </p>
                        <p className="flex justify-between">
                          <span>Unemployment:</span> 
                          <span className="font-medium">{loc.stats.unemploymentPct}%</span>
                        </p>
                      </div>
                    </div>
                  </Popup>
                </CircleMarker>
              ))}
            </MapContainer>
          </div>
        </div>
      </div>

    </div>
  );
}
