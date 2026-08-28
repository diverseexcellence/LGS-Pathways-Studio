import React, { useState } from 'react';
import { Download, FileText, FileSpreadsheet } from 'lucide-react';
import { exportApi } from '../lib/api';

export default function ExportPage() {
  const [exportingStudents, setExportingStudents] = useState(false);
  const [exportingStns, setExportingStns] = useState(false);
  const [status, setStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null);

  async function handleStudentExport() {
    setExportingStudents(true);
    setStatus(null);
    try {
      await exportApi.download();
      setStatus({ type: 'success', message: 'Student export downloaded.' });
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Export failed.' });
    } finally {
      setExportingStudents(false);
    }
  }

  async function handleStnExport() {
    setExportingStns(true);
    setStatus(null);
    try {
      await exportApi.unmatchedStns();
      setStatus({ type: 'success', message: 'Unmatched STN report downloaded.' });
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Export failed.' });
    } finally {
      setExportingStns(false);
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-800">Export</h1>
        <p className="text-slate-500 text-sm mt-1">Download student data and reports</p>
      </div>

      {status && (
        <div className={`p-3 rounded-lg text-sm border ${status.type === 'success' ? 'bg-green-50 text-green-700 border-green-200' : 'bg-red-50 text-red-600 border-red-200'}`}>
          {status.message}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="bg-white rounded-xl border border-slate-200 p-6 shadow-sm">
          <div className="flex items-start gap-4">
            <div className="p-3 bg-blue-50 rounded-lg">
              <FileSpreadsheet className="w-6 h-6 text-blue-600" />
            </div>
            <div className="flex-1">
              <h2 className="font-semibold text-slate-800">Student Roster Export</h2>
              <p className="text-sm text-slate-500 mt-1">Full student list with per-subject ELA and Math tier assignments, demographics, and assessment flags as an Excel (.xlsx) file.</p>
              <button
                onClick={handleStudentExport}
                disabled={exportingStudents}
                className="mt-4 flex items-center gap-2 px-4 py-2 bg-lgs-blue text-white text-sm font-medium rounded-lg hover:bg-lgs-blue-dark transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
              >
                <Download className="w-4 h-4" />
                {exportingStudents ? 'Generating…' : 'Download Excel'}
              </button>
            </div>
          </div>
        </div>

        <div className="bg-white rounded-xl border border-slate-200 p-6 shadow-sm">
          <div className="flex items-start gap-4">
            <div className="p-3 bg-amber-50 rounded-lg">
              <FileText className="w-6 h-6 text-amber-600" />
            </div>
            <div className="flex-1">
              <h2 className="font-semibold text-slate-800">Unmatched STN Report</h2>
              <p className="text-sm text-slate-500 mt-1">Students in uploaded assessment files whose STN could not be matched to any enrolled student.</p>
              <button
                onClick={handleStnExport}
                disabled={exportingStns}
                className="mt-4 flex items-center gap-2 px-4 py-2 bg-amber-600 text-white text-sm font-medium rounded-lg hover:bg-amber-700 transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
              >
                <Download className="w-4 h-4" />
                {exportingStns ? 'Generating…' : 'Download CSV'}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
