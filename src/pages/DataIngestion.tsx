import React, { useState, useEffect } from 'react';
import { Upload, FileText, CheckCircle, AlertCircle, Trash2, History } from 'lucide-react';
import Papa from 'papaparse';
import { doc, setDoc, collection, addDoc, getDocs, deleteDoc, query, orderBy, where } from 'firebase/firestore';
import { db } from '../firebase';
import { useAuth } from '../contexts/AuthContext';
import { handleFirestoreError, OperationType } from '../lib/utils';

export default function DataIngestion() {
  const { role, user } = useAuth();
  const [files, setFiles] = useState<File[]>([]);
  const [uploadType, setUploadType] = useState('demographics');
  const [isUploading, setIsUploading] = useState(false);
  const [status, setStatus] = useState<{ type: 'success' | 'error', message: string } | null>(null);
  const [showClearConfirm, setShowClearConfirm] = useState(false);
  const [isClearing, setIsClearing] = useState(false);
  const [uploadLogs, setUploadLogs] = useState<any[]>([]);
  const [fileToDelete, setFileToDelete] = useState<{id: string, fileName: string} | null>(null);

  useEffect(() => {
    if (role === 'admin') {
      fetchUploadLogs();
    }
  }, [role]);

  const fetchUploadLogs = async () => {
    try {
      const q = query(collection(db, 'upload_logs'), orderBy('date', 'desc'));
      const snapshot = await getDocs(q);
      setUploadLogs(snapshot.docs.map(d => ({ id: d.id, ...d.data() })));
    } catch (error) {
      handleFirestoreError(error, OperationType.GET, 'upload_logs');
    }
  };

  if (role !== 'admin') {
    return <div>Access Denied. Admin only.</div>;
  }

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files.length > 0) {
      setFiles(Array.from(e.target.files));
      setStatus(null);
    }
  };

  const processUpload = async () => {
    if (files.length === 0) return;
    setIsUploading(true);
    setStatus(null);

    let totalProcessed = 0;
    let hasError = false;
    let errorMessage = '';

    for (const currentFile of files) {
      if (uploadLogs.some(log => log.fileName === currentFile.name)) {
        hasError = true;
        errorMessage = `File "${currentFile.name}" has already been uploaded. Please delete it from the history first if you want to re-upload.`;
        break;
      }

      try {
        await new Promise<void>((resolve, reject) => {
          Papa.parse(currentFile, {
            header: true,
            skipEmptyLines: true,
            complete: async (results) => {
              try {
                const data = results.data as any[];
                
                const getVal = (row: any, searchKeys: string[]) => {
                  const actualKeys = Object.keys(row);
                  // Exact match
                  for (const search of searchKeys) {
                    const searchLower = search.toLowerCase();
                    const match = actualKeys.find(k => k.trim().toLowerCase() === searchLower);
                    if (match && row[match]) return row[match];
                  }
                  // Partial match
                  for (const search of searchKeys) {
                    const searchLower = search.toLowerCase();
                    const match = actualKeys.find(k => k.trim().toLowerCase().includes(searchLower));
                    if (match && row[match]) return row[match];
                  }
                  return null;
                };

                for (const row of data) {
                  // Find STN
                  const stn = getVal(row, ['STN', 'State Student ID', 'State_StudentNumber', 'Student State ID', 'Student ID', 'ID']);
                  if (!stn) continue;

                  if (uploadType === 'demographics') {
                    const studentRef = doc(db, 'students', stn);
                    await setDoc(studentRef, {
                      stn,
                      grade: getVal(row, ['Grade', 'Enrolled Grade', 'Grade_Level']) || '',
                      gender: getVal(row, ['Gender']) || '',
                      ethnicity: getVal(row, ['Ethnicity', 'Race']) || '',
                      spedStatus: getVal(row, ['Special Education', 'SPED']) || '',
                      ellStatus: getVal(row, ['English Learner', 'ELL']) || '',
                      section504: getVal(row, ['504']) || '',
                      tier: 'Pending',
                      tierStatus: 'Pending',
                      lastUpdated: new Date().toISOString(),
                      fileName: currentFile.name
                    }, { merge: true });
                  } else {
                    // Assessment data
                    let scoreRaw = getVal(row, ['Score', 'Scale Score', 'Overall Score', 'Diagnostic level', 'Overall level']);
                    let parsedScore = parseFloat(scoreRaw || '0');
                    if (isNaN(parsedScore)) parsedScore = 0;

                    await addDoc(collection(db, 'assessments'), {
                      stn,
                      type: uploadType,
                      date: new Date().toISOString(),
                      subject: getVal(row, ['Subject', 'Content Area']) || 'Mixed',
                      score: parsedScore,
                      proficiency: getVal(row, ['Proficiency Level', 'Status', 'Achievement Level', 'Tier']) || '',
                      details: JSON.stringify(row),
                      fileName: currentFile.name
                    });
                  }
                  totalProcessed++;
                }
                resolve();
              } catch (error) {
                reject(error);
              }
            },
            error: (error) => {
              reject(error);
            }
          });
        });

        // Record successful upload log
        await addDoc(collection(db, 'upload_logs'), {
          fileName: currentFile.name,
          uploadType: uploadType,
          date: new Date().toISOString(),
          userId: user?.uid || 'unknown',
          recordCount: totalProcessed
        });

      } catch (error: any) {
        console.error(error);
        hasError = true;
        errorMessage = error.message || 'An error occurred while processing the files.';
        break;
      }
    }

    if (hasError) {
      setStatus({ type: 'error', message: errorMessage });
    } else {
      setStatus({ type: 'success', message: `Successfully processed ${totalProcessed} records across ${files.length} file(s).` });
      setFiles([]);
      fetchUploadLogs();
    }
    setIsUploading(false);
  };

  const confirmDeleteFile = async () => {
    if (!fileToDelete) return;
    setIsClearing(true);
    setStatus(null);
    try {
      // Delete associated assessments
      const q = query(collection(db, 'assessments'), where('fileName', '==', fileToDelete.fileName));
      const snapshot = await getDocs(q);
      for (const docSnap of snapshot.docs) {
        await deleteDoc(doc(db, 'assessments', docSnap.id));
      }
      
      // Delete the log
      await deleteDoc(doc(db, 'upload_logs', fileToDelete.id));
      
      setStatus({ type: 'success', message: `Successfully deleted ${fileToDelete.fileName} and its associated assessments.` });
      fetchUploadLogs();
    } catch (error) {
      handleFirestoreError(error, OperationType.DELETE, 'upload_logs');
      setStatus({ type: 'error', message: 'Failed to delete file record.' });
    } finally {
      setIsClearing(false);
      setFileToDelete(null);
    }
  };

  const clearDatabase = async () => {
    setIsClearing(true);
    setStatus(null);
    try {
      const collectionsToClear = ['students', 'assessments', 'audit_logs', 'notes', 'upload_logs'];
      for (const colName of collectionsToClear) {
        const q = query(collection(db, colName));
        const snapshot = await getDocs(q);
        for (const document of snapshot.docs) {
          await deleteDoc(doc(db, colName, document.id));
        }
      }
      setStatus({ type: 'success', message: 'Database cleared successfully.' });
      fetchUploadLogs();
    } catch (error) {
      handleFirestoreError(error, OperationType.DELETE, 'multiple_collections');
      setStatus({ type: 'error', message: 'Failed to clear database.' });
    } finally {
      setIsClearing(false);
      setShowClearConfirm(false);
    }
  };

  return (
    <div className="max-w-3xl mx-auto space-y-8">
      {fileToDelete && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-md w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-2">Delete File Record</h3>
            <p className="text-slate-600 text-sm mb-6">
              Are you sure you want to delete the upload record for <strong>{fileToDelete.fileName}</strong>? 
              This will also permanently remove any assessment records associated with this file.
            </p>
            <div className="flex justify-end gap-3">
              <button
                onClick={() => setFileToDelete(null)}
                disabled={isClearing}
                className="px-4 py-2 text-slate-700 font-medium hover:bg-slate-100 rounded-lg transition-colors disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={confirmDeleteFile}
                disabled={isClearing}
                className="px-4 py-2 bg-red-600 text-white font-medium hover:bg-red-700 rounded-lg transition-colors disabled:opacity-50"
              >
                {isClearing ? 'Deleting...' : 'Delete File'}
              </button>
            </div>
          </div>
        </div>
      )}
      <div>
        <h1 className="text-2xl font-bold text-lgs-blue">Data Ingestion</h1>
        <p className="text-slate-500 mt-1">Securely upload student demographic and assessment data.</p>
      </div>

      <div className="bg-white p-8 rounded-xl shadow-sm border border-slate-200">
        <div className="space-y-6">
          <div>
            <label className="block text-sm font-medium text-slate-700 mb-2">Data Type</label>
            <select
              value={uploadType}
              onChange={(e) => setUploadType(e.target.value)}
              className="w-full px-4 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue outline-none"
            >
              <option value="demographics">PowerSchool Demographics</option>
              <option value="ILEARN">ILEARN Checkpoint Data</option>
              <option value="IXL">IXL Diagnostic Data</option>
              <option value="Acadience">Acadience Reading Data</option>
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 mb-2">Upload CSV File</label>
            <div className="mt-1 flex justify-center px-6 pt-5 pb-6 border-2 border-slate-300 border-dashed rounded-lg hover:border-lgs-blue transition-colors">
              <div className="space-y-1 text-center">
                <FileText className="mx-auto h-12 w-12 text-slate-400" />
                <div className="flex text-sm text-slate-600 justify-center">
                  <label htmlFor="file-upload" className="relative cursor-pointer bg-white rounded-md font-medium text-lgs-blue hover:text-lgs-blue-dark focus-within:outline-none focus-within:ring-2 focus-within:ring-offset-2 focus-within:ring-lgs-blue">
                    <span>Upload a file</span>
                    <input id="file-upload" name="file-upload" type="file" accept=".csv" multiple className="sr-only" onChange={handleFileChange} />
                  </label>
                  <p className="pl-1">or drag and drop</p>
                </div>
                <p className="text-xs text-slate-500">CSV up to 10MB per file</p>
              </div>
            </div>
            {files.length > 0 && (
              <div className="mt-4 space-y-2 max-h-32 overflow-y-auto">
                <p className="text-sm font-medium text-slate-700">Selected Files ({files.length}):</p>
                {files.map((f, index) => (
                  <p key={index} className="text-sm text-slate-600 flex items-center gap-2">
                    <CheckCircle className="w-4 h-4 text-green-500 shrink-0" />
                    <span className="truncate">{f.name}</span>
                  </p>
                ))}
              </div>
            )}
          </div>

          <button
            onClick={processUpload}
            disabled={files.length === 0 || isUploading}
            className="w-full flex items-center justify-center gap-2 px-4 py-2 bg-lgs-red text-white rounded-lg font-medium hover:bg-lgs-red-dark disabled:opacity-50 transition-colors"
          >
            <Upload className="w-5 h-5" />
            {isUploading ? 'Processing...' : 'Upload Data'}
          </button>

          {status && (
            <div className={`p-4 rounded-lg flex items-start gap-3 ${status.type === 'success' ? 'bg-green-50 text-green-800' : 'bg-red-50 text-red-800'}`}>
              {status.type === 'success' ? <CheckCircle className="w-5 h-5 mt-0.5" /> : <AlertCircle className="w-5 h-5 mt-0.5" />}
              <p className="text-sm font-medium">{status.message}</p>
            </div>
          )}
        </div>
      </div>

      {/* Upload Logs Section */}
      <div className="bg-white p-8 rounded-xl shadow-sm border border-slate-200">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
          <History className="w-5 h-5 text-lgs-red" />
          Upload History
        </h2>
        <div className="overflow-x-auto">
          <table className="w-full text-sm text-left">
            <thead className="bg-slate-50 text-slate-600 font-medium border-b border-slate-200">
              <tr>
                <th className="px-4 py-3">Date</th>
                <th className="px-4 py-3">File Name</th>
                <th className="px-4 py-3">Type</th>
                <th className="px-4 py-3 text-right">Records Processed</th>
                <th className="px-4 py-3 text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {uploadLogs.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-6 text-center text-slate-500">
                    No files have been uploaded yet.
                  </td>
                </tr>
              ) : (
                uploadLogs.map((log) => (
                  <tr key={log.id} className="hover:bg-slate-50">
                    <td className="px-4 py-3">{new Date(log.date).toLocaleString()}</td>
                    <td className="px-4 py-3 font-medium text-slate-900">{log.fileName}</td>
                    <td className="px-4 py-3 text-slate-600">{log.uploadType}</td>
                    <td className="px-4 py-3 text-right">{log.recordCount}</td>
                    <td className="px-4 py-3 text-right">
                      <button
                        onClick={() => setFileToDelete({ id: log.id, fileName: log.fileName })}
                        className="text-red-600 hover:text-red-800 p-1 rounded hover:bg-red-50 transition-colors"
                        title="Delete File"
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

      <div className="bg-red-50 p-8 rounded-xl shadow-sm border border-red-200">
        <h2 className="text-lg font-semibold text-red-900 mb-2 flex items-center gap-2">
          <Trash2 className="w-5 h-5" />
          Danger Zone
        </h2>
        <p className="text-red-700 text-sm mb-4">
          This action will permanently delete all students, assessments, notes, and audit logs. This cannot be undone.
        </p>
        {!showClearConfirm ? (
          <button
            onClick={() => setShowClearConfirm(true)}
            className="px-4 py-2 bg-red-600 text-white rounded-lg font-medium hover:bg-red-700 transition-colors"
          >
            Clear All Data
          </button>
        ) : (
          <div className="flex items-center gap-4">
            <button
              onClick={clearDatabase}
              disabled={isClearing}
              className="px-4 py-2 bg-red-700 text-white rounded-lg font-medium hover:bg-red-800 disabled:opacity-50 transition-colors"
            >
              {isClearing ? 'Deleting...' : 'Yes, delete everything'}
            </button>
            <button
              onClick={() => setShowClearConfirm(false)}
              disabled={isClearing}
              className="px-4 py-2 bg-white text-slate-700 border border-slate-300 rounded-lg font-medium hover:bg-slate-50 disabled:opacity-50 transition-colors"
            >
              Cancel
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
