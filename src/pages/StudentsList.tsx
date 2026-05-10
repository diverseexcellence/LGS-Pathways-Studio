import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { AgGridReact } from 'ag-grid-react';
import { ColDef, GridReadyEvent, IGetRowsParams, GridApi } from 'ag-grid-community';
import 'ag-grid-community/styles/ag-grid.css';
import 'ag-grid-community/styles/ag-theme-quartz.css';
import { studentsApi, Student } from '../lib/api';
import { Users, Search, RefreshCw } from 'lucide-react';

const tierCellRenderer = (params: { value: string }) => {
  const tier = params.value || '';
  const cls =
    tier === 'Tier 1' ? 'bg-green-100 text-green-700' :
    tier === 'Tier 2' ? 'bg-yellow-100 text-yellow-700' :
    tier === 'Tier 3' ? 'bg-red-100 text-red-700' :
    'bg-slate-100 text-slate-600';
  return `<span class="px-2 py-0.5 rounded-full text-xs font-medium ${cls}">${tier || 'Pending'}</span>`;
};

const statusCellRenderer = (params: { value: boolean }) =>
  `<span class="text-xs ${params.value ? 'text-green-600' : 'text-slate-400'}">${params.value ? 'Active' : 'Inactive'}</span>`;

export default function StudentsList() {
  const navigate = useNavigate();
  const gridRef = useRef<AgGridReact<Student>>(null);
  const [gridApi, setGridApi] = useState<GridApi | null>(null);
  const [search, setSearch] = useState('');
  const [totalCount, setTotalCount] = useState(0);
  const [isLoading, setIsLoading] = useState(false);
  const searchRef = useRef(search);
  searchRef.current = search;

  const columnDefs: ColDef<Student>[] = [
    {
      field: 'fullName',
      headerName: 'Name',
      sortable: true,
      filter: true,
      flex: 2,
      minWidth: 160,
    },
    {
      field: 'classGroup',
      headerName: 'Class',
      sortable: true,
      filter: true,
      flex: 1,
      minWidth: 100,
    },
    {
      field: 'grade',
      headerName: 'Grade',
      sortable: true,
      flex: 1,
      minWidth: 80,
    },
    {
      field: 'tier',
      headerName: 'Tier',
      sortable: true,
      filter: true,
      flex: 1,
      minWidth: 100,
      cellRenderer: (params: any) => tierCellRenderer(params),
    },
    {
      field: 'isActive',
      headerName: 'Status',
      sortable: true,
      flex: 1,
      minWidth: 90,
      cellRenderer: (params: any) => statusCellRenderer(params),
    },
    {
      headerName: 'Action',
      flex: 1,
      minWidth: 120,
      sortable: false,
      filter: false,
      cellRenderer: (params: any) => {
        if (!params.data) return '';
        return `<button data-id="${params.data.studentId}" class="text-lgs-red hover:underline text-sm font-medium view-profile-btn">View Profile →</button>`;
      },
    },
  ];

  const defaultColDef: ColDef = {
    resizable: true,
  };

  // Server-side datasource
  const datasource = useCallback(() => ({
    getRows: async (params: IGetRowsParams) => {
      setIsLoading(true);
      try {
        const page = Math.floor(params.startRow / 50) + 1;
        const sortModel = params.sortModel?.[0];
        const result = await studentsApi.list({
          page,
          pageSize: 50,
          search: searchRef.current || undefined,
          sortBy: sortModel?.colId,
          sortDir: sortModel?.sort as 'asc' | 'desc' | undefined,
        });
        setTotalCount(result.total);
        params.successCallback(result.items, result.total);
        gridRef.current?.api?.hideOverlay();
      } catch {
        params.failCallback();
      } finally {
        setIsLoading(false);
      }
    },
  }), []);

  const onGridReady = useCallback((event: GridReadyEvent) => {
    setGridApi(event.api);
    event.api.setGridOption('datasource', datasource());
  }, [datasource]);

  // Re-trigger datasource when search changes
  useEffect(() => {
    if (gridApi) {
      gridApi.setGridOption('datasource', datasource());
    }
  }, [search, gridApi, datasource]);

  const handleRefresh = () => {
    if (gridApi) gridApi.setGridOption('datasource', datasource());
  };

  // Handle row-level "View Profile" button clicks via event delegation
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      const target = (e.target as HTMLElement).closest('.view-profile-btn') as HTMLElement | null;
      if (target) {
        const id = target.getAttribute('data-id');
        if (id) navigate(`/students/${id}`);
      }
    };
    document.addEventListener('click', handler);
    return () => document.removeEventListener('click', handler);
  }, [navigate]);

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
        <button
          onClick={handleRefresh}
          className="flex items-center gap-2 px-4 py-2 text-sm font-medium text-slate-600 border border-slate-300 rounded-lg hover:bg-slate-50 transition-colors"
        >
          <RefreshCw className={`w-4 h-4 ${isLoading ? 'animate-spin' : ''}`} />
          Refresh
        </button>
      </div>

      {/* Search */}
      <div className="bg-white p-4 rounded-xl shadow-sm border border-slate-200">
        <div className="relative max-w-md">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
          <input
            type="text"
            placeholder="Search by name or class…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="w-full pl-10 pr-4 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none text-sm"
          />
        </div>
      </div>

      {/* AG Grid */}
      <div
        className="ag-theme-quartz rounded-xl overflow-hidden shadow-sm border border-slate-200"
        style={{ height: 600 }}
      >
        <AgGridReact<Student>
          ref={gridRef}
          columnDefs={columnDefs}
          defaultColDef={defaultColDef}
          rowModelType="infinite"
          cacheBlockSize={50}
          maxBlocksInCache={10}
          onGridReady={onGridReady}
          suppressCellFocus={true}
          rowHeight={48}
          headerHeight={44}
          overlayLoadingTemplate='<span class="text-slate-500 text-sm">Loading students…</span>'
          overlayNoRowsTemplate='<span class="text-slate-500 text-sm">No students found. Upload data to get started.</span>'
        />
      </div>
    </div>
  );
}
