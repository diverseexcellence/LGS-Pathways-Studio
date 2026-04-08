import React, { useState, useEffect, useMemo } from 'react';
import { Link } from 'react-router-dom';
import { collection, getDocs } from 'firebase/firestore';
import { db } from '../firebase';
import { handleFirestoreError, OperationType } from '../lib/utils';
import { Users, ChevronRight, ArrowUpDown, ArrowUp, ArrowDown } from 'lucide-react';

export default function StudentsList() {
  const [students, setStudents] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [sortConfig, setSortConfig] = useState<{ key: string, direction: 'asc' | 'desc' } | null>(null);

  useEffect(() => {
    const fetchStudents = async () => {
      try {
        const snapshot = await getDocs(collection(db, 'students'));
        setStudents(snapshot.docs.map(d => ({ id: d.id, ...d.data() })));
      } catch (error) {
        handleFirestoreError(error, OperationType.GET, 'students');
      } finally {
        setLoading(false);
      }
    };
    fetchStudents();
  }, []);

  const sortedStudents = useMemo(() => {
    let sortableStudents = [...students];
    if (sortConfig !== null) {
      sortableStudents.sort((a, b) => {
        let aValue = a[sortConfig.key] || '';
        let bValue = b[sortConfig.key] || '';
        
        if (sortConfig.key === 'grade') {
           const aNum = parseInt(aValue);
           const bNum = parseInt(bValue);
           if (!isNaN(aNum) && !isNaN(bNum)) {
             aValue = aNum;
             bValue = bNum;
           }
        }

        if (aValue < bValue) {
          return sortConfig.direction === 'asc' ? -1 : 1;
        }
        if (aValue > bValue) {
          return sortConfig.direction === 'asc' ? 1 : -1;
        }
        return 0;
      });
    }
    return sortableStudents;
  }, [students, sortConfig]);

  const requestSort = (key: string) => {
    let direction: 'asc' | 'desc' = 'asc';
    if (sortConfig && sortConfig.key === key && sortConfig.direction === 'asc') {
      direction = 'desc';
    }
    setSortConfig({ key, direction });
  };

  const getSortIcon = (key: string) => {
    if (!sortConfig || sortConfig.key !== key) {
      return <ArrowUpDown className="w-4 h-4 ml-1 text-slate-400" />;
    }
    if (sortConfig.direction === 'asc') {
      return <ArrowUp className="w-4 h-4 ml-1 text-lgs-blue" />;
    }
    return <ArrowDown className="w-4 h-4 ml-1 text-lgs-blue" />;
  };

  if (loading) return <div className="p-8">Loading students...</div>;

  return (
    <div className="space-y-8 max-w-5xl mx-auto">
      <div>
        <h1 className="text-2xl font-bold text-lgs-blue flex items-center gap-2">
          <Users className="w-6 h-6 text-lgs-red" />
          Student Directory
        </h1>
        <p className="text-slate-500 mt-1">View all students and their current tier status.</p>
      </div>

      <div className="bg-white rounded-xl shadow-sm border border-slate-200 overflow-hidden">
        <table className="w-full text-sm text-left">
          <thead className="bg-slate-50 text-slate-600 font-medium border-b border-slate-200 select-none">
            <tr>
              <th className="px-6 py-4 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestSort('stn')}>
                <div className="flex items-center">STN {getSortIcon('stn')}</div>
              </th>
              <th className="px-6 py-4 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestSort('grade')}>
                <div className="flex items-center">Grade {getSortIcon('grade')}</div>
              </th>
              <th className="px-6 py-4 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestSort('tier')}>
                <div className="flex items-center">Tier {getSortIcon('tier')}</div>
              </th>
              <th className="px-6 py-4 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestSort('tierStatus')}>
                <div className="flex items-center">Status {getSortIcon('tierStatus')}</div>
              </th>
              <th className="px-6 py-4 text-right">Action</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {sortedStudents.length === 0 ? (
              <tr>
                <td colSpan={5} className="px-6 py-8 text-center text-slate-500">
                  No students found. Upload data to get started.
                </td>
              </tr>
            ) : (
              sortedStudents.map((student) => (
                <tr key={student.id} className="hover:bg-slate-50 transition-colors">
                  <td className="px-6 py-4 font-medium text-slate-900">{student.stn}</td>
                  <td className="px-6 py-4 text-slate-600">{student.grade || 'N/A'}</td>
                  <td className="px-6 py-4">
                    <span className={`px-2 py-1 rounded-full text-xs font-medium ${
                      student.tier === 'Tier 1' ? 'bg-green-100 text-green-700' :
                      student.tier === 'Tier 2' ? 'bg-yellow-100 text-yellow-700' :
                      student.tier === 'Tier 3' ? 'bg-red-100 text-red-700' :
                      'bg-slate-100 text-slate-700'
                    }`}>
                      {student.tier}
                    </span>
                  </td>
                  <td className="px-6 py-4 text-slate-600">{student.tierStatus}</td>
                  <td className="px-6 py-4 text-right">
                    <Link
                      to={`/students/${student.stn}`}
                      className="inline-flex items-center text-lgs-red hover:text-lgs-red-dark font-medium"
                    >
                      View Profile
                      <ChevronRight className="w-4 h-4 ml-1" />
                    </Link>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
