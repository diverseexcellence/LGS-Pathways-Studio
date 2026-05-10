import React, { useState, useEffect, useRef } from 'react';
import { Upload, FileText, CheckCircle, AlertCircle, Trash2, History, XCircle, CloudDownload } from 'lucide-react';
import { uploadApi, exportApi, ParseSummary, UploadLog } from '../lib/api';

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
  const [isExporting, setIsExporting] = useState(false);
  const [isImporting, setIsImporting] = useState(false);
  const [importResults, setImportResults] = useState<{ file: string; uploadType?: string; result?: ParseSummary; error?: string }[] | null>(null);
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

    for (const file of files) {
      try {
        const summary = await uploadApi.upload(file, uploadType);
        lastSummary = summary;
        totalImported += summary.importedRows;
      } catch (err: any) {
        setStatus({ type: 'error', message: err.message || 'Upload failed' });
        setIsUploading(false);
        return;
      }
    }

    setParseSummary(lastSummary);
    setStatus({
      type: 'success',
      message: `Successfully imported ${totalImported} records across ${files.length} file(s).`,
    });
    setFiles([]);
    if (fileInputRef.current) fileInputRef.current.value = '';
    await fetchLogs();
    setIsUploading(false);
  };

  const confirmDelete = async () => {
    if (!logToDelete) return;
    setIsDeleting(true);
    try {
      await uploadApi.deleteLog(logToDelete.id);
      setUploadLogs((prev) => prev.filter((l) => l.exportId !== logToDelete.id));
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
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Import failed' });
    } finally {
      setIsImporting(false);
    }
  };

  const handleExport = async () => {
    setIsExporting(true);
    try {
      await exportApi.download();
    } catch (err: any) {
      setStatus({ type: 'error', message: err.message || 'Export failed' });
    } finally {
      setIsExporting(false);
    }
  };

  return (
    <div className="max-w-3xl mx-auto space-y-8">
      {/* Delete confirmation modal */}
      {logToDelete && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-md w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-2">Delete Upload Record</h3>
            <p className="text-slate-600 text-sm mb-6">
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
        <p className="text-slate-500 mt-1 text-sm">
          Upload CSV or Excel student data files. Files are parsed server-side and stored securely in Azure SQL.
        </p>
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

        <div className="border-t border-slate-200 pt-4">
          <p className="text-xs text-slate-500 mb-3">
            Or import all files already staged in the Azure <strong>landing-zone</strong> container.
            Upload type is detected automatically from each filename.
          </p>
          <button
            onClick={handleImportLandingZone}
            disabled={isImporting}
            className="w-full flex items-center justify-center gap-2 px-4 py-2.5 bg-lgs-blue text-white rounded-lg font-medium hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
          >
            <CloudDownload className="w-5 h-5" />
            {isImporting ? 'Importing from Landing Zone…' : 'Import from Landing Zone'}
          </button>
        </div>

        {importResults && importResults.length > 0 && (
          <div className="bg-slate-50 rounded-lg p-4 border border-slate-200 text-sm space-y-2">
            <p className="font-semibold text-slate-700">Landing Zone Import Results</p>
            {importResults.map((r, i) => (
              <div key={i} className={`rounded px-3 py-2 text-xs ${r.error ? 'bg-red-50 border border-red-200 text-red-700' : 'bg-green-50 border border-green-200 text-green-800'}`}>
                <span className="font-medium">{r.file}</span>
                {r.error
                  ? ` — Error: ${r.error}`
                  : ` (${r.uploadType}) — ${r.result?.importedRows ?? 0} imported, ${r.result?.skippedRows ?? 0} skipped`}
              </div>
            ))}
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
            {parseSummary.duplicates.length > 0 && (
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

      {/* Upload History */}
      <div className="bg-white p-8 rounded-xl shadow-sm border border-slate-200">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-lgs-blue flex items-center gap-2">
            <History className="w-5 h-5 text-lgs-red" />
            Upload History
          </h2>
          <button
            onClick={handleExport}
            disabled={isExporting}
            className="flex items-center gap-2 px-4 py-2 text-sm font-medium bg-lgs-blue text-white rounded-lg hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
          >
            {isExporting ? 'Exporting…' : 'Export All Students (.xlsx)'}
          </button>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm text-left">
            <thead className="bg-slate-50 text-slate-600 font-medium border-b border-slate-200">
              <tr>
                <th className="px-4 py-3">Date</th>
                <th className="px-4 py-3">File Name</th>
                <th className="px-4 py-3">Type</th>
                <th className="px-4 py-3 text-right">Records</th>
                <th className="px-4 py-3 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {uploadLogs.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-8 text-center text-slate-400 text-sm">
                    No files uploaded yet.
                  </td>
                </tr>
              ) : (
                uploadLogs.map((log) => (
                  <tr key={log.id} className="hover:bg-slate-50 transition-colors">
                    <td className="px-4 py-3 text-slate-500">{new Date(log.uploadedAt).toLocaleString()}</td>
                    <td className="px-4 py-3 font-medium text-slate-900">{log.fileName}</td>
                    <td className="px-4 py-3 text-slate-600">{log.uploadType}</td>
                    <td className="px-4 py-3 text-right text-slate-700">{log.recordCount.toLocaleString()}</td>
                    <td className="px-4 py-3 text-right">
                      <button
                        onClick={() => setLogToDelete(log)}
                        className="p-1.5 text-red-500 hover:text-red-700 hover:bg-red-50 rounded transition-colors"
                        title="Delete"
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
