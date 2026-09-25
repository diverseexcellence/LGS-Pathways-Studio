import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { AgGridReact } from 'ag-grid-react';
import { ColDef, SortChangedEvent } from 'ag-grid-community';
import 'ag-grid-community/styles/ag-grid.css';
import 'ag-grid-community/styles/ag-theme-quartz.css';
import { studentsApi, exportApi, Student, SubjectTier } from '../lib/api';
import { Users, Search, RefreshCw, Download, UserPlus, ChevronLeft, ChevronRight, Pencil } from 'lucide-react';

const PAGE_SIZES = [25, 50, 100];

// Same provenance cue as the student profile: a filled pill is an Admin Override, an outlined
// pill is the system recommendation. "Finalized" is the pre-rename name for an override.
function isAdminOverride(status?: string | null) {
  return status === 'Admin Override' || status === 'Finalized';
}

const SubjectTierCell = ({ value }: { value: SubjectTier | undefined }) => {
  const tier = value?.tier || '';
  const override = isAdminOverride(value?.status);
  const cls = override
    ? tier === 'Tier 1' ? 'bg-green-600 text-white border-green-600'
      : tier === 'Tier 2' ? 'bg-yellow-600 text-white border-yellow-600'
      : tier === 'Tier 3' ? 'bg-red-600 text-white border-red-600'
      : 'bg-slate-500 text-white border-slate-500'
    : tier === 'Tier 1' ? 'bg-white text-green-700 border-green-400'
      : tier === 'Tier 2' ? 'bg-white text-yellow-700 border-yellow-400'
      : tier === 'Tier 3' ? 'bg-white text-red-700 border-red-400'
      : 'bg-slate-100 text-slate-600 border-slate-300';
  const title = value?.status === 'Pending'
    ? value.pendingReason === 'no_assessments' ? 'No assessment data yet.'
      : value.pendingReason === 'insufficient_data_points' ? `Only ${value.dataPoints} of the required data points are available.`
      : value.pendingReason === 'all_evidence_excluded' ? 'Assessment data present but none of it is usable evidence.'
      : 'Pending / Review — not enough evidence for an automatic tier.'
    : override ? 'Admin Override' : value?.status || undefined;
  return (
    <span
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-semibold border-2 whitespace-nowrap ${cls}`}
      title={title}
    >
      {override && <Pencil className="w-3 h-3 shrink-0" />}
      {tier || 'Pending'}
    </span>
  );
};

const StatusCell = ({ value }: { value: boolean }) => (
  <span className={`text-xs ${value ? 'text-green-600' : 'text-slate-400'}`}>{value ? 'Active' : 'Inactive'}</span>
);

function pageWindow(current: number, total: number): (number | 'gap')[] {
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);
  const pages: (number | 'gap')[] = [1];
  const start = Math.max(2, current - 1);
  const end = Math.min(total - 1, current + 1);
  if (start > 2) pages.push('gap');
  for (let i = start; i <= end; i++) pages.push(i);
  if (end < total - 1) pages.push('gap');
  pages.push(total);
  return pages;
}

export default function StudentsList() {
  const navigate = useNavigate();
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [students, setStudents] = useState<Student[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [sortBy, setSortBy] = useState<string | undefined>();
  const [sortDir, setSortDir] = useState<'asc' | 'desc' | undefined>();
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [refreshKey, setRefreshKey] = useState(0);
  const [isExporting, setIsExporting] = useState(false);

  const columnDefs: ColDef<Student>[] = [
    {
      field: 'fullName',
      headerName: 'Name',
      sortable: true,
      comparator: () => 0,
      flex: 2,
      minWidth: 160,
    },
    {
      field: 'stn',
      headerName: 'STN',
      sortable: true,
      comparator: () => 0,
      flex: 1,
      minWidth: 110,
      cellRenderer: (params: any) =>
        params.value
          ? <span className="font-mono text-xs text-slate-700">{params.value}</span>
          : <span className="text-slate-300 text-xs">—</span>,
    },
    {
      field: 'grade',
      headerName: 'Grade',
      sortable: true,
      comparator: () => 0,
      flex: 1,
      minWidth: 80,
    },
    {
      field: 'elaTier',
      headerName: 'ELA Tier',
      sortable: true,
      comparator: () => 0,
      flex: 1,
      minWidth: 110,
      cellRenderer: (params: any) => <SubjectTierCell value={params.value} />,
    },
    {
      field: 'mathTier',
      headerName: 'Math Tier',
      sortable: true,
      comparator: () => 0,
      flex: 1,
      minWidth: 110,
      cellRenderer: (params: any) => <SubjectTierCell value={params.value} />,
    },
    {
      field: 'isActive',
      headerName: 'Status',
      sortable: true,
      comparator: () => 0,
      flex: 1,
      minWidth: 90,
      cellRenderer: (params: any) => <StatusCell value={params.value} />,
    },
    {
      headerName: 'Action',
      flex: 1,
      minWidth: 120,
      sortable: false,
      filter: false,
      cellRenderer: (params: any) => {
        if (!params.data) return null;
        return (
          <button
            onClick={() => navigate(`/students/${params.data.studentId}`)}
            className="text-lgs-red hover:underline text-sm font-medium"
          >
            View Profile →
          </button>
        );
      },
    },
  ];

  const defaultColDef: ColDef = {
    resizable: true,
  };

  useEffect(() => {
    const next = searchInput.trim();
    const timer = window.setTimeout(() => {
      setSearch(current => {
        if (current === next) return current;
        setPage(1);
        return next;
      });
    }, 300);
    return () => window.clearTimeout(timer);
  }, [searchInput]);

  useEffect(() => {
    let cancelled = false;
    setIsLoading(true);
    setLoadError('');
    studentsApi.list({
      page,
      pageSize,
      search: search || undefined,
      sortBy,
      sortDir,
    }).then(result => {
      if (cancelled) return;
      setStudents(result.items);
      setTotalCount(result.total);
    }).catch(() => {
      if (cancelled) return;
      setStudents([]);
      setTotalCount(0);
      setLoadError('Could not load students. Try Refresh.');
    }).finally(() => {
      if (!cancelled) setIsLoading(false);
    });
    return () => { cancelled = true; };
  }, [page, pageSize, search, sortBy, sortDir, refreshKey]);

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const rangeFrom = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const rangeTo = Math.min(page * pageSize, totalCount);

  const handleRefresh = () => setRefreshKey(k => k + 1);

  const handleSortChanged = (event: SortChangedEvent<Student>) => {
    const sorted = event.api.getColumnState().find(col => col.sort);
    const nextBy = sorted?.colId;
    const nextDir = sorted?.sort === 'desc' ? 'desc' : sorted?.sort === 'asc' ? 'asc' : undefined;
    if (nextBy === sortBy && nextDir === sortDir) return;
    setSortBy(nextBy);
    setSortDir(nextDir);
    setPage(1);
  };

  const handleExport = async () => {
    setIsExporting(true);
    try { await exportApi.download(); }
    catch { /* user-visible failure not critical */ }
    finally { setIsExporting(false); }
  };


  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-lgs-blue flex items-center gap-2">
            <Users className="w-6 h-6 text-lgs-red" />
            Student Directory
          </h1>
          <p className="text-slate-500 mt-1 text-sm">
            {isLoading && totalCount === 0 ? 'Loading…' : `${totalCount.toLocaleString()} students`}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => navigate('/students/new')}
            className="flex items-center gap-2 px-4 py-2 text-sm font-medium bg-lgs-red text-white rounded-lg hover:bg-lgs-red-dark transition-colors"
          >
            <UserPlus className="w-4 h-4" />
            Add Student
          </button>
          <button
            onClick={handleExport}
            disabled={isExporting}
            className="flex items-center gap-2 px-4 py-2 text-sm font-medium bg-lgs-blue text-white rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
          >
            <Download className="w-4 h-4" />
            {isExporting ? 'Exporting…' : 'Export (.xlsx)'}
          </button>
          <button
            onClick={handleRefresh}
            className="flex items-center gap-2 px-4 py-2 text-sm font-medium text-slate-600 border border-slate-300 rounded-lg hover:bg-slate-50 transition-colors"
          >
            <RefreshCw className={`w-4 h-4 ${isLoading ? 'animate-spin' : ''}`} />
            Refresh
          </button>
        </div>
      </div>

      {/* Search */}
      <div className="bg-white p-4 rounded-xl shadow-sm border border-slate-200">
        <div className="relative max-w-md">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
          <input
            type="text"
            placeholder="Search by name, STN, or class…"
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            className="w-full pl-10 pr-4 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none text-sm"
          />
        </div>
      </div>

      {/* AG Grid + pagination. One server page at a time; sorting is applied by the API. */}
      <div className="bg-white rounded-xl overflow-hidden shadow-sm border border-slate-200">
        {loadError && (
          <p className="px-4 py-2 text-sm text-red-600 bg-red-50 border-b border-red-100">{loadError}</p>
        )}
        <div className="ag-theme-quartz" style={{ minHeight: students.length === 0 ? 240 : undefined }}>
          <AgGridReact<Student>
            columnDefs={columnDefs}
            defaultColDef={defaultColDef}
            rowData={students}
            loading={isLoading}
            domLayout="autoHeight"
            onSortChanged={handleSortChanged}
            suppressCellFocus={true}
            rowHeight={48}
            headerHeight={44}
            overlayLoadingTemplate='<span class="text-slate-500 text-sm">Loading students…</span>'
            overlayNoRowsTemplate='<span class="text-slate-500 text-sm">No students found.</span>'
            getRowId={params => params.data.studentId}
          />
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-3 border-t border-slate-200">
          <p className="text-sm text-slate-500">
            {totalCount === 0
              ? 'No students'
              : `Showing ${rangeFrom.toLocaleString()}–${rangeTo.toLocaleString()} of ${totalCount.toLocaleString()}`}
          </p>
          <div className="flex items-center gap-1">
            <button
              type="button"
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={page <= 1 || isLoading}
              className="p-1.5 rounded-lg text-slate-600 hover:bg-slate-100 disabled:opacity-40 disabled:hover:bg-transparent"
              aria-label="Previous page"
            >
              <ChevronLeft className="w-4 h-4" />
            </button>
            {pageWindow(Math.min(page, totalPages), totalPages).map((item, index) =>
              item === 'gap' ? (
                <span key={`gap-${index}`} className="px-1 text-slate-400 text-sm">…</span>
              ) : (
                <button
                  key={item}
                  type="button"
                  onClick={() => setPage(item)}
                  disabled={isLoading}
                  aria-current={item === page ? 'page' : undefined}
                  className={`min-w-8 h-8 px-2 rounded-lg text-sm font-medium ${
                    item === page
                      ? 'bg-lgs-blue text-white'
                      : 'text-slate-600 hover:bg-slate-100'
                  } disabled:opacity-40`}
                >
                  {item}
                </button>
              )
            )}
            <button
              type="button"
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages || isLoading}
              className="p-1.5 rounded-lg text-slate-600 hover:bg-slate-100 disabled:opacity-40 disabled:hover:bg-transparent"
              aria-label="Next page"
            >
              <ChevronRight className="w-4 h-4" />
            </button>
          </div>
          <label className="flex items-center gap-2 text-sm text-slate-500">
            Rows
            <select
              value={pageSize}
              onChange={e => { setPageSize(Number(e.target.value)); setPage(1); }}
              className="px-2 py-1 border border-slate-300 rounded-lg text-sm text-slate-700 bg-white focus:ring-2 focus:ring-lgs-blue outline-none"
            >
              {PAGE_SIZES.map(size => (
                <option key={size} value={size}>{size}</option>
              ))}
            </select>
          </label>
        </div>
      </div>
    </div>
  );
}
