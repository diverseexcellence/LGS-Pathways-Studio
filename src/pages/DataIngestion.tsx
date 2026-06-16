import React, { useState, useEffect, useRef, useMemo } from 'react';
import { Upload, FileText, CheckCircle, AlertCircle, Trash2, History, XCircle, Search, X, ShieldAlert, Download, RefreshCw } from 'lucide-react';
import { uploadApi, exportApi, unmatchedStnsApi, ParseSummary, UploadLog, UnmatchedStnRow } from '../lib/api';

const UPLOAD_TYPES = [
  { value: 'demographics', label: 'PowerSchool Demographics' },
  { value: 'ILEARN', label: 'ILEARN Checkpoint Data' },
  { value: 'IXL', label: 'IXL Diagnostic Data' },
  { value: 'Acadience', label: 'Acadience Reading Data' },
  { value: 'IREAD', label: 'IREAD Data' },
  { value: 'WIDA', label: 'WIDA Data' },
];

export default function DataIngestion() {
  const [files, setFiles] = useState<File[]>([]);
  const [uploadType, setUploadType] = useState('demographics');
  const [isUploading, setIsUploading] = useState(false);
  const [parseSummary, setParseSummary] = useState<ParseSummary | null>(null);
  const [status, setStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
  const [uploadLogs, setUploadLogs] = useState<UploadLog[]>([]);
  const [logToDelete, setLogToDelete] = useState<UploadLog | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [isDragging, setIsDragging] = useState(false);
  const [isImporting, setIsImporting] = useState(false);
  const [importResults, setImportResults] = useState<{ file: string; uploadType?: string; result?: ParseSummary; error?: string }[] | null>(null);
  const [historySearch, setHistorySearch] = useState('');
  const [historyTypeFilter, setHistoryTypeFilter] = useState('');
  const [isExportingStns, setIsExportingStns] = useState(false);
  const [isRecalculating, setIsRecalculating] = useState(false);
  const [recalcStatus, setRecalcStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
  const [stnExportStatus, setStnExportStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null);
  const [unmatchedRows, setUnmatchedRows] = useState<UnmatchedStnRow[] | null>(null);
  const [unmatchedTotal, setUnmatchedTotal] = useState<number | null>(null);
  const [loadingUnmatched, setLoadingUnmatched] = useState(false);
  const [stnSearch, setStnSearch] = useState('');
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    fetchLogs();
  }, []);

  const fetchLogs = async () => {
    try {
      const logs = await uploadApi.logs();
      setUploadLogs(logs);
    } catch {
      // non-blocking
    }
  };

  const addFiles = (incoming: FileList | File[]) => {
    const valid = Array.from(incoming).filter(
      (f) => f.name.endsWith('.csv') || f.name.endsWith('.xlsx')
    );
    setFiles((prev) => {
      const names = new Set(prev.map((f) => f.name));
      return [...prev, ...valid.filter((f) => !names.has(f.name))];
    });
    setStatus(null);
    setParseSummary(null);
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) addFiles(e.target.files);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    addFiles(e.dataTransfer.files);
  };

  const removeFile = (name: string) => {
    setFiles((prev) => prev.filter((f) => f.name !== name));
  };

  const processUpload = async () => {
    if (files.length === 0) return;
    setIsUploading(true);
    setStatus(null);
    setParseSummary(null);

    let lastSummary: ParseSummary | null = null;
    let totalImported = 0;
    const fileErrors: string[] = [];

    for (const file of files) {
      try {
        const summary = await uploadApi.upload(file, uploadType);
        lastSummary = summary;
        totalImported += summary.importedRows;
      } catch (err: any) {
        fileErrors.push(`${file.name}: ${err.message || 'Upload failed'}`);
      }
    }

    const succeeded = files.length - fileErrors.length;
    setParseSummary(lastSummary);

    if (fileErrors.length > 0 && succeeded === 0) {
      setStatus({ type: 'error', message: fileErrors.join(' | ') });
      setIsUploading(false);
      return;
    }

    setStatus({
      type: fileErrors.length > 0 ? 'error' : 'success',
      message: fileErrors.length > 0
        ? `${succeeded} of ${files.length} file(s) imported (${totalImported} records). Errors: ${fileErrors.join(' | ')}`
        : `Successfully imported ${totalImported} records across ${files.length} file(s).`,
    });
    setFiles([]);
    if (fileInputRef.current) fileInputRef.current.value = '';
    await fetchLogs();

    // Auto-recalculate tiers after every upload
    try {
      setIsRecalculating(true);
      const res = await uploadApi.recalculateTiers();
      setRecalcStatus({ type: 'success', message: res.message });
    } catch {
      setRecalcStatus({ type: 'error', message: 'Tier recalculation failed after upload.' });
    } finally {
      setIsRecalculating(false);
    }

    setIsUploading(false);
  };

  const confirmDelete = async () => {
    if (!logToDelete) return;
    setIsDeleting(true);
    try {
      await uploadApi.deleteLog(logToDelete.id);
      setUploadLogs((prev) => prev.filter((l) => l.id !== logToDelete.id));
      setStatus({ type: 'success', message: `Deleted upload record for ${logToDelete.fileName}.` });
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Delete failed' });
    } finally {
      setIsDeleting(false);
      setLogToDelete(null);
    }
  };

  const handleImportLandingZone = async () => {
    setIsImporting(true);
    setImportResults(null);
    setStatus(null);
    try {
      const res = await uploadApi.importLandingZone();
      setImportResults(res.results);
      const total = res.results.reduce((sum, r) => sum + (r.result?.importedRows ?? 0), 0);
      setStatus({ type: 'success', message: `${res.message} ${total} record(s) imported.` });
      await fetchLogs();

      // Auto-recalculate tiers after landing zone import
      try {
        setIsRecalculating(true);
        const recalc = await uploadApi.recalculateTiers();
        setRecalcStatus({ type: 'success', message: recalc.message });
      } catch {
        setRecalcStatus({ type: 'error', message: 'Tier recalculation failed after import.' });
      } finally {
        setIsRecalculating(false);
      }
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Import failed' });
    } finally {
      setIsImporting(false);
    }
  };


  const handleExportUnmatchedStns = async () => {
    setIsExportingStns(true);
    setStnExportStatus(null);
    try {
      await exportApi.unmatchedStns();
      setStnExportStatus({ type: 'success', message: 'Unmatched STN report downloaded.' });
    } catch (err: any) {
      setStnExportStatus({ type: 'error', message: err.message || 'Export failed' });
    } finally {
      setIsExportingStns(false);
    }
  };

  const handleLoadUnmatchedList = async () => {
    setLoadingUnmatched(true);
    try {
      const result = await unmatchedStnsApi.list();
      setUnmatchedRows(result.rows);
      setUnmatchedTotal(result.total);
      setStnSearch('');
    } catch (err: any) {
      setStnExportStatus({ type: 'error', message: err.message || 'Failed to load unmatched STNs' });
    } finally {
      setLoadingUnmatched(false);
    }
  };

  const logTypes = useMemo(() =>
    [...new Set(uploadLogs.map((l) => l.uploadType))].sort(),
  [uploadLogs]);

  const filteredLogs = useMemo(() => {
    const q = historySearch.trim().toLowerCase();
    return uploadLogs.filter((l) => {
      if (historyTypeFilter && l.uploadType !== historyTypeFilter) return false;
      if (q && !l.fileName.toLowerCase().includes(q) && !l.uploadType.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [uploadLogs, historySearch, historyTypeFilter]);

  return (
    <div className="max-w-3xl mx-auto space-y-8">
      {/* Delete confirmation modal */}
      {logToDelete && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-md w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-2">Delete Upload Record</h3>
            <p className="text-slate-600 text-sm mb-6 break-all">
              Delete upload record for <strong>{logToDelete.fileName}</strong>? Associated assessment
              records will also be removed from the database.
            </p>
            <div className="flex justify-end gap-3">
              <button
                onClick={() => setLogToDelete(null)}
                disabled={isDeleting}
                className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={confirmDelete}
                disabled={isDeleting}
                className="px-4 py-2 bg-red-600 text-white font-medium hover:bg-red-700 rounded-lg transition-colors disabled:opacity-50"
              >
                {isDeleting ? 'Deleting…' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}

      <div>
        <h1 className="text-2xl font-bold text-lgs-blue">Data Upload</h1>
      </div>

      {/* Upload Card */}
      <div className="bg-white p-8 rounded-xl shadow-sm border border-slate-200 space-y-6">
        {/* Data type selector */}
        <div>
          <label className="block text-sm font-medium text-slate-700 mb-2">Data Type</label>
          <select
            value={uploadType}
            onChange={(e) => setUploadType(e.target.value)}
            className="w-full px-4 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none text-sm"
          >
            {UPLOAD_TYPES.map((t) => (
              <option key={t.value} value={t.value}>{t.label}</option>
            ))}
          </select>
        </div>

        {/* Drop zone */}
        <div>
          <label className="block text-sm font-medium text-slate-700 mb-2">Upload File</label>
          <div
            onDragOver={(e) => { e.preventDefault(); setIsDragging(true); }}
            onDragLeave={() => setIsDragging(false)}
            onDrop={handleDrop}
            onClick={() => fileInputRef.current?.click()}
            className={`mt-1 flex justify-center px-6 pt-8 pb-8 border-2 border-dashed rounded-lg cursor-pointer transition-colors ${
              isDragging ? 'border-lgs-blue bg-blue-50' : 'border-slate-300 hover:border-lgs-blue'
            }`}
          >
            <div className="space-y-2 text-center">
              <FileText className="mx-auto h-10 w-10 text-slate-400" />
              <p className="text-sm text-slate-600">
                <span className="font-medium text-lgs-blue">Click to browse</span> or drag & drop
              </p>
              <p className="text-xs text-slate-400">.csv or .xlsx — up to 10 MB per file</p>
            </div>
            <input
              ref={fileInputRef}
              type="file"
              accept=".csv,.xlsx"
              multiple
              className="sr-only"
              onChange={handleFileChange}
            />
          </div>

          {/* Selected files list */}
          {files.length > 0 && (
            <div className="mt-4 space-y-2">
              <p className="text-xs font-semibold text-slate-500 uppercase tracking-wide">
                Selected ({files.length})
              </p>
              {files.map((f) => (
                <div key={f.name} className="flex items-center justify-between text-sm text-slate-700 bg-slate-50 rounded-lg px-3 py-2">
                  <div className="flex items-center gap-2">
                    <CheckCircle className="w-4 h-4 text-green-500 shrink-0" />
                    <span className="truncate">{f.name}</span>
                    <span className="text-slate-400 text-xs shrink-0">
                      ({(f.size / 1024).toFixed(0)} KB)
                    </span>
                  </div>
                  <button onClick={() => removeFile(f.name)} className="ml-2 text-slate-400 hover:text-red-500">
                    <XCircle className="w-4 h-4" />
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>

        <button
          onClick={processUpload}
          disabled={files.length === 0 || isUploading}
          className="w-full flex items-center justify-center gap-2 px-4 py-2.5 bg-lgs-red text-white rounded-lg font-medium hover:bg-lgs-red-dark disabled:opacity-50 transition-colors"
        >
          <Upload className="w-5 h-5" />
          {isUploading ? 'Uploading & Parsing…' : 'Upload Data'}
        </button>


        {isRecalculating && (
          <div className="p-3 rounded-lg text-sm border flex items-center gap-2 bg-slate-50 text-slate-600 border-slate-200">
            <RefreshCw className="w-4 h-4 shrink-0 animate-spin" />
            Recalculating tiers…
          </div>
        )}
        {!isRecalculating && recalcStatus && (
          <div className={`p-3 rounded-lg text-sm border flex items-center gap-2 ${recalcStatus.type === 'success' ? 'bg-green-50 text-green-700 border-green-200' : 'bg-red-50 text-red-600 border-red-200'}`}>
            {recalcStatus.type === 'success' ? <CheckCircle className="w-4 h-4 shrink-0" /> : <AlertCircle className="w-4 h-4 shrink-0" />}
            Tiers recalculated: {recalcStatus.message}
          </div>
        )}

        {importResults && importResults.length > 0 && (
          <div className="bg-slate-50 rounded-lg p-4 border border-slate-200 text-sm space-y-2">
            <p className="font-semibold text-slate-700">Landing Zone Import Results</p>
            <div className="max-h-72 overflow-y-auto space-y-2 pr-1">
              {importResults.map((r, i) => (
                <div key={i} className={`rounded px-3 py-2 text-xs ${r.error ? 'bg-red-50 border border-red-200 text-red-700' : 'bg-green-50 border border-green-200 text-green-800'}`}>
                  <span className="font-medium break-all">{r.file}</span>
                  {r.error
                    ? ` — Error: ${r.error}`
                    : ` (${r.uploadType}) — ${r.result?.importedRows ?? 0} imported, ${r.result?.skippedRows ?? 0} skipped`}
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Status message */}
        {status && (
          <div className={`p-4 rounded-lg flex items-start gap-3 ${status.type === 'success' ? 'bg-green-50 text-green-800 border border-green-200' : 'bg-red-50 text-red-800 border border-red-200'}`}>
            {status.type === 'success'
              ? <CheckCircle className="w-5 h-5 mt-0.5 shrink-0" />
              : <AlertCircle className="w-5 h-5 mt-0.5 shrink-0" />}
            <p className="text-sm font-medium">{status.message}</p>
          </div>
        )}

        {/* Parse summary */}
        {parseSummary && (
          <div className="bg-slate-50 rounded-lg p-4 border border-slate-200 text-sm space-y-2">
            <p className="font-semibold text-slate-700">Parse Summary</p>
            <div className="grid grid-cols-3 gap-4 text-center">
              <div>
                <p className="text-2xl font-bold text-lgs-blue">{parseSummary.totalRows}</p>
                <p className="text-xs text-slate-500">Total Rows</p>
              </div>
              <div>
                <p className="text-2xl font-bold text-green-600">{parseSummary.importedRows}</p>
                <p className="text-xs text-slate-500">Imported</p>
              </div>
              <div>
                <p className="text-2xl font-bold text-yellow-600">{parseSummary.skippedRows}</p>
                <p className="text-xs text-slate-500">Skipped</p>
              </div>
            </div>
            {(parseSummary.duplicates?.length ?? 0) > 0 && (
              <p className="text-xs text-yellow-700 bg-yellow-50 border border-yellow-200 rounded px-3 py-2 mt-2">
                {parseSummary.duplicates.length} duplicate(s) detected and skipped.
              </p>
            )}
            {parseSummary.errors.length > 0 && (
              <div className="text-xs text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 mt-2 space-y-1">
                {parseSummary.errors.slice(0, 5).map((e, i) => <p key={i}>{e}</p>)}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Data Quality — BRD DI-10 */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2 mb-1">
          <ShieldAlert className="w-5 h-5 text-lgs-red" />
          Data Quality
        </h2>
        <p className="text-sm text-slate-500 mb-4">
          Assessment records whose STN does not match any enrolled student. Use this to identify
          file mismatches or missing demographic uploads.
        </p>
        <div className="flex flex-wrap gap-2 mb-4">
          <button
            onClick={handleLoadUnmatchedList}
            disabled={loadingUnmatched}
            className="flex items-center gap-2 px-4 py-2.5 bg-lgs-blue text-white rounded-lg font-medium hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors text-sm"
          >
            <ShieldAlert className="w-4 h-4" />
            {loadingUnmatched ? 'Loading…' : unmatchedRows !== null ? 'Refresh' : 'View Unmatched STNs'}
          </button>
          {unmatchedRows !== null && unmatchedRows.length > 0 && (
            <button
              onClick={handleExportUnmatchedStns}
              disabled={isExportingStns}
              className="flex items-center gap-2 px-4 py-2.5 border border-slate-300 text-slate-700 rounded-lg font-medium hover:bg-slate-50 disabled:opacity-50 transition-colors text-sm"
            >
              <Download className="w-4 h-4" />
              {isExportingStns ? 'Downloading…' : 'Download CSV'}
            </button>
          )}
        </div>
        {stnExportStatus && (
          <div className={`mb-4 p-3 rounded-lg flex items-center gap-2 text-sm ${
            stnExportStatus.type === 'success'
              ? 'bg-green-50 text-green-800 border border-green-200'
              : 'bg-red-50 text-red-800 border border-red-200'
          }`}>
            {stnExportStatus.type === 'success'
              ? <CheckCircle className="w-4 h-4 shrink-0" />
              : <AlertCircle className="w-4 h-4 shrink-0" />}
            {stnExportStatus.message}
          </div>
        )}
        {unmatchedRows !== null && (
          <div>
            <div className="flex items-center justify-between mb-3">
              <p className="text-sm font-medium text-slate-700">
                {unmatchedTotal === 0
                  ? 'No unmatched STNs found.'
                  : `${unmatchedTotal} unmatched STN${unmatchedTotal !== 1 ? 's' : ''} across uploaded assessment files`}
              </p>
              {unmatchedRows.length > 0 && (
                <div className="relative">
                  <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-slate-400 pointer-events-none" />
                  <input
                    type="text"
                    placeholder="Filter STN or file…"
                    value={stnSearch}
                    onChange={e => setStnSearch(e.target.value)}
                    className="pl-8 pr-3 py-1.5 text-sm border border-slate-200 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none w-52"
                  />
                </div>
              )}
            </div>
            {unmatchedRows.length > 0 && (
              <div className="overflow-x-auto rounded-lg border border-slate-200">
                <table className="w-full text-xs">
                  <thead className="bg-slate-50 text-slate-600 uppercase tracking-wide text-[11px]">
                    <tr>
                      <th className="px-3 py-2 text-left font-semibold">STN</th>
                      <th className="px-3 py-2 text-left font-semibold">Upload Type</th>
                      <th className="px-3 py-2 text-left font-semibold">File Name</th>
                      <th className="px-3 py-2 text-left font-semibold">Uploaded At</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {unmatchedRows
                      .filter(r => {
                        if (!stnSearch) return true;
                        const q = stnSearch.toLowerCase();
                        return r.stn.toLowerCase().includes(q) || r.fileName.toLowerCase().includes(q);
                      })
                      .slice(0, 200)
                      .map((r, i) => (
                        <tr key={i} className="hover:bg-slate-50">
                          <td className="px-3 py-2 font-mono text-slate-800">{r.stn}</td>
                          <td className="px-3 py-2 text-slate-600">{r.uploadType}</td>
                          <td className="px-3 py-2 text-slate-500 break-all max-w-[200px]">{r.fileName}</td>
                          <td className="px-3 py-2 text-slate-400 whitespace-nowrap">
                            {new Date(r.uploadedAt).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
                          </td>
                        </tr>
                      ))}
                  </tbody>
                </table>
                {unmatchedRows.filter(r => {
                  if (!stnSearch) return true;
                  const q = stnSearch.toLowerCase();
                  return r.stn.toLowerCase().includes(q) || r.fileName.toLowerCase().includes(q);
                }).length > 200 && (
                  <p className="px-3 py-2 text-xs text-slate-400 border-t border-slate-100">
                    Showing first 200 results. Download CSV for the full list.
                  </p>
                )}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Upload History */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2 mb-4">
          <History className="w-5 h-5 text-lgs-red" />
          Upload History
          {uploadLogs.length > 0 && (
            <span className="text-xs font-normal text-slate-400 ml-1">
              {filteredLogs.length !== uploadLogs.length
                ? `${filteredLogs.length} of ${uploadLogs.length}`
                : uploadLogs.length}
            </span>
          )}
        </h2>

        {/* Search + filter bar */}
        {uploadLogs.length > 0 && (
          <div className="flex gap-2 mb-3">
            <div className="relative flex-1">
              <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-slate-400 pointer-events-none" />
              <input
                type="text"
                placeholder="Search file name…"
                value={historySearch}
                onChange={(e) => setHistorySearch(e.target.value)}
                className="w-full pl-8 pr-8 py-1.5 text-sm border border-slate-200 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
              />
              {historySearch && (
                <button
                  onClick={() => setHistorySearch('')}
                  className="absolute right-2 top-1/2 -translate-y-1/2 text-slate-300 hover:text-slate-500"
                >
                  <X className="w-3.5 h-3.5" />
                </button>
              )}
            </div>
            <select
              value={historyTypeFilter}
              onChange={(e) => setHistoryTypeFilter(e.target.value)}
              className="px-3 py-1.5 text-sm border border-slate-200 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none text-slate-600 bg-white"
            >
              <option value="">All types</option>
              {logTypes.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
            {(historySearch || historyTypeFilter) && (
              <button
                onClick={() => { setHistorySearch(''); setHistoryTypeFilter(''); }}
                className="px-3 py-1.5 text-xs text-slate-400 hover:text-slate-600 border border-slate-200 rounded-lg transition-colors"
              >
                Clear
              </button>
            )}
          </div>
        )}

        {/* Scrollable table — fixed height so it never overflows the page */}
        <div className="rounded-lg border border-slate-200 overflow-hidden">
          <div className="overflow-y-auto max-h-72">
            <table className="w-full text-sm text-left table-fixed">
              <colgroup>
                <col className="w-[130px]" />
                <col />
                <col className="w-[100px]" />
                <col className="w-[76px]" />
                <col className="w-[44px]" />
              </colgroup>
              <thead className="bg-slate-50 text-slate-500 text-xs font-semibold uppercase tracking-wide border-b border-slate-200 sticky top-0 z-10">
                <tr>
                  <th className="px-3 py-2.5">Date</th>
                  <th className="px-3 py-2.5">File Name</th>
                  <th className="px-3 py-2.5">Type</th>
                  <th className="px-3 py-2.5 text-right">Records</th>
                  <th className="px-3 py-2.5"></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {uploadLogs.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="px-4 py-8 text-center text-slate-400 text-sm">
                      No files uploaded yet.
                    </td>
                  </tr>
                ) : filteredLogs.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="px-4 py-6 text-center text-slate-400 text-sm">
                      No results match your search.
                    </td>
                  </tr>
                ) : (
                  filteredLogs.map((log) => (
                    <tr key={log.id} className="hover:bg-slate-50 transition-colors">
                      <td className="px-3 py-2 text-slate-400 text-xs whitespace-nowrap">
                        {new Date(log.uploadedAt).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: '2-digit' })}
                        <span className="block text-slate-300">
                          {new Date(log.uploadedAt).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
                        </span>
                      </td>
                      <td className="px-3 py-2 font-medium text-slate-800">
                        <span className="block truncate" title={log.fileName}>
                          {log.fileName}
                        </span>
                      </td>
                      <td className="px-3 py-2">
                        <span className="inline-block px-2 py-0.5 rounded-full text-xs font-medium bg-slate-100 text-slate-600 truncate max-w-full">
                          {log.uploadType}
                        </span>
                      </td>
                      <td className="px-3 py-2 text-right text-slate-600 tabular-nums">
                        {log.recordCount.toLocaleString()}
                      </td>
                      <td className="px-3 py-2 text-right">
                        <button
                          onClick={() => setLogToDelete(log)}
                          className="p-1 text-slate-300 hover:text-red-500 hover:bg-red-50 rounded transition-colors"
                          title={`Delete ${log.fileName}`}
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>
        {filteredLogs.length > 0 && (
          <p className="mt-2 text-right text-xs text-slate-300">
            Scroll to see all {filteredLogs.length} {filteredLogs.length === 1 ? 'entry' : 'entries'}
          </p>
        )}
      </div>

    </div>
  );
}
