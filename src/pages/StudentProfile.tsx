import React, { useState, useEffect, useMemo } from 'react';
import { useParams } from 'react-router-dom';
import { doc, getDoc, collection, query, where, getDocs, orderBy, updateDoc, addDoc, deleteDoc } from 'firebase/firestore';
import { db } from '../firebase';
import { useAuth } from '../contexts/AuthContext';
import { handleFirestoreError, OperationType } from '../lib/utils';
import { User, BookOpen, Clock, AlertTriangle, CheckCircle, MessageSquare, Info, FileJson, Trash2, ArrowUpDown, ArrowUp, ArrowDown } from 'lucide-react';
import { GoogleGenAI } from '@google/genai';

const ai = new GoogleGenAI({ apiKey: process.env.GEMINI_API_KEY });

export default function StudentProfile() {
  const { stn } = useParams<{ stn: string }>();
  const { role, user } = useAuth();
  const [student, setStudent] = useState<any>(null);
  const [assessments, setAssessments] = useState<any[]>([]);
  const [notes, setNotes] = useState<any[]>([]);
  const [auditLogs, setAuditLogs] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [newNote, setNewNote] = useState('');
  const [isGeneratingTier, setIsGeneratingTier] = useState(false);
  const [overrideTier, setOverrideTier] = useState('');
  const [showDemographics, setShowDemographics] = useState(false);
  const [selectedAssessment, setSelectedAssessment] = useState<any>(null);
  const [noteToDelete, setNoteToDelete] = useState<string | null>(null);
  const [assessmentSortConfig, setAssessmentSortConfig] = useState<{ key: string, direction: 'asc' | 'desc' } | null>(null);

  useEffect(() => {
    if (stn) {
      fetchStudentData();
    }
  }, [stn]);

  const fetchStudentData = async () => {
    setLoading(true);
    try {
      // Fetch student
      const studentDoc = await getDoc(doc(db, 'students', stn!));
      if (studentDoc.exists()) {
        setStudent(studentDoc.data());
      }

      // Fetch assessments
      const qAssessments = query(collection(db, 'assessments'), where('stn', '==', stn));
      const assessSnapshot = await getDocs(qAssessments);
      setAssessments(assessSnapshot.docs.map(d => ({ id: d.id, ...d.data() })));

      // Fetch notes
      const qNotes = query(collection(db, 'notes'), where('stn', '==', stn));
      const notesSnapshot = await getDocs(qNotes);
      setNotes(notesSnapshot.docs.map(d => ({ id: d.id, ...d.data() })).sort((a: any, b: any) => new Date(b.date).getTime() - new Date(a.date).getTime()));

      // Fetch audit logs
      const qLogs = query(collection(db, 'audit_logs'), where('stn', '==', stn));
      const logsSnapshot = await getDocs(qLogs);
      setAuditLogs(logsSnapshot.docs.map(d => ({ id: d.id, ...d.data() })).sort((a: any, b: any) => new Date(b.date).getTime() - new Date(a.date).getTime()));

    } catch (error) {
      handleFirestoreError(error, OperationType.GET, 'student_data');
    } finally {
      setLoading(false);
    }
  };

  const getAssessmentDisplayData = (a: any) => {
    let rawDetails: any = {};
    try {
      rawDetails = JSON.parse(a.details || '{}');
    } catch (e) {}

    const getVal = (searchKeys: string[]) => {
      const actualKeys = Object.keys(rawDetails);
      // Exact match (case-insensitive, trimmed)
      for (const search of searchKeys) {
        const searchLower = search.toLowerCase();
        const match = actualKeys.find(k => k.trim().toLowerCase() === searchLower);
        if (match && rawDetails[match]) return { value: rawDetails[match], key: match };
      }
      // Partial match
      for (const search of searchKeys) {
        const searchLower = search.toLowerCase();
        const match = actualKeys.find(k => k.trim().toLowerCase().includes(searchLower));
        if (match && rawDetails[match]) return { value: rawDetails[match], key: match };
      }
      return null;
    };

    const typeObj = getVal(['Test Reason', 'Assessment Name', 'Test Name']);
    let type = typeObj ? typeObj.value : a.type;
    
    if (a.type === 'IXL' || (a.fileName && a.fileName.includes('IXL'))) {
      if (a.fileName && a.fileName.includes('ELA')) {
        type = 'IXL-ELA';
      } else if (a.fileName && a.fileName.includes('Math')) {
        type = 'IXL-Math';
      }
    }
    
    const dateObj = getVal(['Date of completion', 'Date Taken', 'Test Date', 'Date']);
    let dateStr = dateObj ? dateObj.value : a.date;

    if (a.type === 'IXL' || (a.fileName && a.fileName.includes('IXL'))) {
      if (typeof dateStr === 'string') {
        // Just remove the parenthesis characters
        dateStr = dateStr.replace(/[()]/g, '').trim();
        // Try to extract just the date part (e.g. MM/DD/YYYY or YYYY-MM-DD)
        const dateMatch = dateStr.match(/(\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}|\d{4}[\/\-]\d{1,2}[\/\-]\d{1,2})/);
        if (dateMatch) {
          dateStr = dateMatch[1];
        }
      }
    }

    const subjectObj = getVal(['Subject', 'Test Name', 'Content Area']);
    let subject = subjectObj ? subjectObj.value : a.subject || 'Mixed';
    
    if (a.type === 'IXL' || (a.fileName && a.fileName.includes('IXL'))) {
      if (a.fileName && a.fileName.includes('ELA')) {
        subject = 'ELA';
      } else if (a.fileName && a.fileName.includes('Math')) {
        subject = 'Math';
      }
    } else if (a.fileName && a.fileName.match(/Mathematics/i)) {
      subject = 'Math';
    } else if (a.fileName && a.fileName.match(/English/i)) {
      subject = 'ELA';
    } else if (subject.match(/ELA|English|Language/i)) {
      subject = 'ELA';
    } else if (subject.match(/Math/i)) {
      subject = 'Math';
    }

    const profObj = getVal(['Performance Level', 'Proficiency Level', 'Status', 'Achievement Level']);
    let proficiency = profObj ? profObj.value : a.proficiency || 'N/A';
    if (typeof proficiency === 'string') {
      const pLower = proficiency.toLowerCase();
      if (pLower.includes('below')) {
        proficiency = 'Below Proficiency';
      } else if (pLower.includes('approaching')) {
        proficiency = 'Approaching Proficiency';
      } else if (pLower.includes('above')) {
        proficiency = 'Above Proficiency';
      } else if (pLower.includes('at prof') || pLower === 'at' || pLower === 'proficient' || pLower.includes('at proficiency')) {
        proficiency = 'At Proficiency';
      }
    }

    let formattedDate = dateStr;
    try {
      const d = new Date(dateStr);
      if (!isNaN(d.getTime())) {
        formattedDate = d.toLocaleDateString();
      }
    } catch (e) {}

    const scoreObj = getVal(['Scale Score', 'Score', 'Overall Score', 'Overall ELA score']);
    const score = scoreObj ? scoreObj.value : a.score;
    const scoreSource = scoreObj ? scoreObj.key : 'Default Score';

    return {
      type,
      formattedDate,
      subject,
      proficiency,
      score,
      scoreSource,
      rawDetails
    };
  };

  const generateTierRecommendation = async () => {
    if (!student || assessments.length === 0) return;
    setIsGeneratingTier(true);
    try {
      const toFloat = (value: any): number | null => {
        if (value == null) return null;
        const s = String(value).trim().replace("%", "");
        if (!s) return null;
        const f = parseFloat(s);
        return isNaN(f) ? null : f;
      };

      const interpretOnOrAboveFromPercentile = (percentile: any): boolean | null => {
        const p = toFloat(percentile);
        if (p === null) return null;
        return p >= 40.0;
      };

      const interpretOnOrAboveFromTier = (tier: any): boolean | null => {
        if (tier == null) return null;
        const s = String(tier).trim().toLowerCase();
        if (!s) return null;

        if (s.includes("far") && s.includes("below")) return false;
        if (s.includes("below")) return false;
        if (s.includes("approaching")) return false;

        if (s.includes("above")) return true;
        if (s.includes("on") && s.includes("grade")) return true;
        if (s.includes("at") && s.includes("grade")) return true;
        if (s.includes("at") && s.includes("prof")) return true;
        if (s.includes("meets") || s.includes("exceeds")) return true;
        if (s.includes("proficient")) return true;

        const m = s.match(/tier\s*(\d+)/);
        if (m) {
          const n = parseInt(m[1], 10);
          if (n === 1) return true;
          return false;
        }

        if (s.includes("mid") && s.includes("above")) return true;
        if (s.includes("early") && s.includes("on")) return true;

        return null;
      };

      const computeTierRecommendation = (elaOnOrAbove: boolean | null, mathOnOrAbove: boolean | null): string | null => {
        if (elaOnOrAbove === null && mathOnOrAbove === null) return null;
        
        const ela = elaOnOrAbove !== null ? elaOnOrAbove : mathOnOrAbove;
        const math = mathOnOrAbove !== null ? mathOnOrAbove : elaOnOrAbove;

        if (ela && math) return "Tier 1";
        if (!ela && !math) return "Tier 3";
        return "Tier 2";
      };

      const sortedAssessments = [...assessments].sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime());
      
      let elaAssessment = null;
      let mathAssessment = null;

      for (const a of sortedAssessments) {
        const displayData = getAssessmentDisplayData(a);
        if (!elaAssessment && displayData.subject === 'ELA') {
          elaAssessment = displayData;
        }
        if (!mathAssessment && displayData.subject === 'Math') {
          mathAssessment = displayData;
        }
      }

      const elaOnOrAbove = interpretOnOrAboveFromTier(elaAssessment?.proficiency);
      
      let mathOnOrAbove = null;
      if (mathAssessment) {
        const actualKeys = Object.keys(mathAssessment.rawDetails);
        const percentileKey = actualKeys.find(k => k.toLowerCase().includes('percentile'));
        const mathPercentile = percentileKey ? mathAssessment.rawDetails[percentileKey] : null;
        
        mathOnOrAbove = interpretOnOrAboveFromPercentile(mathPercentile);
        if (mathOnOrAbove === null) {
          mathOnOrAbove = interpretOnOrAboveFromTier(mathAssessment.proficiency);
        }
      }

      const recommendedTier = computeTierRecommendation(elaOnOrAbove, mathOnOrAbove);

      // Generate AI Chronological Summary
      let aiSummaryText = '';
      try {
        // Fetch all assessments to calculate LGS (Local Grade/School) averages
        const allAssessmentsSnap = await getDocs(collection(db, 'assessments'));
        let elaTotal = 0, elaCount = 0, mathTotal = 0, mathCount = 0;
        allAssessmentsSnap.docs.forEach(docSnap => {
          const a = docSnap.data();
          const d = getAssessmentDisplayData(a);
          const score = parseFloat(d.score);
          if (!isNaN(score) && score > 0) {
            if (d.subject === 'ELA') { elaTotal += score; elaCount++; }
            if (d.subject === 'Math') { mathTotal += score; mathCount++; }
          }
        });
        const elaAvg = elaCount > 0 ? (elaTotal / elaCount).toFixed(1) : 'N/A';
        const mathAvg = mathCount > 0 ? (mathTotal / mathCount).toFixed(1) : 'N/A';

        const prompt = `
          Create a concise, chronological summary report of the student's academic progress based on the following assessment data.
          Highlight trends, strengths, and areas of concern. 
          Also, compare the student's performance to the overall LGS (Local Grade/School) averages provided below.
          Do not use markdown headers, just a clean text summary.
          
          Student Grade: ${student.grade}
          LGS Averages: ELA = ${elaAvg}, Math = ${mathAvg}
          
          Assessments (Chronological):
          ${JSON.stringify(sortedAssessments.map(a => {
            const d = getAssessmentDisplayData(a);
            return { date: d.formattedDate, type: d.type, subject: d.subject, score: d.score, proficiency: d.proficiency };
          }))}
        `;

        const response = await ai.models.generateContent({
          model: 'gemini-2.5-flash',
          contents: prompt,
        });

        aiSummaryText = response.text?.trim() || '';
      } catch (aiError: any) {
        console.error("AI Summary Generation Error", aiError);
        alert("Tier recommendation was generated, but the AI Summary failed to generate: " + (aiError.message || "Unknown error"));
      }

      if (recommendedTier) {
        let reason = `Based on ELA (${elaAssessment?.proficiency || 'N/A'} -> ${elaOnOrAbove ? 'On/Above' : 'Below'}) and Math (${mathAssessment?.proficiency || 'N/A'} -> ${mathOnOrAbove ? 'On/Above' : 'Below'}).`;
        
        await updateDoc(doc(db, 'students', stn!), {
          tier: recommendedTier,
          tierStatus: 'System Recommended',
          lastUpdated: new Date().toISOString()
        });

        await addDoc(collection(db, 'audit_logs'), {
          stn: stn!,
          date: new Date().toISOString(),
          action: 'System Tier Recommendation Generated',
          userId: user?.uid,
          details: `Recommended ${recommendedTier}. Reason: ${reason}`
        });

        if (aiSummaryText) {
          await addDoc(collection(db, 'notes'), {
            stn: stn!,
            date: new Date().toISOString(),
            authorId: user?.uid,
            authorName: 'AI Assistant',
            role: 'system',
            content: aiSummaryText,
            type: 'AI Summary'
          });
        }

        fetchStudentData();
      } else {
        alert("Could not determine a tier recommendation based on the available assessment data. Ensure both ELA and Math assessments have proficiency or percentile data.");
      }
    } catch (error) {
      console.error("Tier Generation Error", error);
      alert("Error generating tier recommendation.");
    } finally {
      setIsGeneratingTier(false);
    }
  };

  const handleOverrideTier = async () => {
    if (!overrideTier || !student) return;
    try {
      await updateDoc(doc(db, 'students', stn!), {
        tier: overrideTier,
        tierStatus: 'Finalized',
        lastUpdated: new Date().toISOString()
      });

      await addDoc(collection(db, 'audit_logs'), {
        stn: stn!,
        date: new Date().toISOString(),
        action: 'Tier Overridden/Finalized by Admin',
        userId: user?.uid,
        details: `Tier set to ${overrideTier}`
      });

      setOverrideTier('');
      fetchStudentData();
    } catch (error) {
      handleFirestoreError(error, OperationType.UPDATE, 'students');
    }
  };

  const handleAddNote = async () => {
    if (!newNote.trim() || !student) return;
    try {
      await addDoc(collection(db, 'notes'), {
        stn: stn!,
        date: new Date().toISOString(),
        authorId: user?.uid,
        authorName: user?.displayName || user?.email,
        role: role,
        content: newNote.trim(),
        type: role === 'admin' ? 'Admin Note' : 'Teacher Note'
      });
      setNewNote('');
      fetchStudentData();
    } catch (error) {
      handleFirestoreError(error, OperationType.CREATE, 'notes');
    }
  };

  const confirmDeleteNote = async () => {
    if (!noteToDelete) return;
    try {
      await deleteDoc(doc(db, 'notes', noteToDelete));
      setNotes(notes.filter(n => n.id !== noteToDelete));
      setNoteToDelete(null);
    } catch (error) {
      handleFirestoreError(error, OperationType.DELETE, 'notes');
    }
  };

  const sortedAssessmentsTable = useMemo(() => {
    const displayAssessments = assessments.map(a => ({ ...a, displayData: getAssessmentDisplayData(a) }));
    if (assessmentSortConfig !== null) {
      displayAssessments.sort((a, b) => {
        let aValue = a.displayData[assessmentSortConfig.key] || '';
        let bValue = b.displayData[assessmentSortConfig.key] || '';
        
        if (assessmentSortConfig.key === 'score') {
           const aNum = parseFloat(aValue);
           const bNum = parseFloat(bValue);
           if (!isNaN(aNum) && !isNaN(bNum)) {
             aValue = aNum;
             bValue = bNum;
           }
        } else if (assessmentSortConfig.key === 'formattedDate') {
           const aDate = new Date(aValue).getTime();
           const bDate = new Date(bValue).getTime();
           if (!isNaN(aDate) && !isNaN(bDate)) {
             aValue = aDate;
             bValue = bDate;
           }
        }

        if (aValue < bValue) {
          return assessmentSortConfig.direction === 'asc' ? -1 : 1;
        }
        if (aValue > bValue) {
          return assessmentSortConfig.direction === 'asc' ? 1 : -1;
        }
        return 0;
      });
    }
    return displayAssessments;
  }, [assessments, assessmentSortConfig]);

  const requestAssessmentSort = (key: string) => {
    let direction: 'asc' | 'desc' = 'asc';
    if (assessmentSortConfig && assessmentSortConfig.key === key && assessmentSortConfig.direction === 'asc') {
      direction = 'desc';
    }
    setAssessmentSortConfig({ key, direction });
  };

  const getAssessmentSortIcon = (key: string) => {
    if (!assessmentSortConfig || assessmentSortConfig.key !== key) {
      return <ArrowUpDown className="w-4 h-4 ml-1 text-slate-400" />;
    }
    if (assessmentSortConfig.direction === 'asc') {
      return <ArrowUp className="w-4 h-4 ml-1 text-lgs-blue" />;
    }
    return <ArrowDown className="w-4 h-4 ml-1 text-lgs-blue" />;
  };

  if (loading) return <div className="p-8">Loading student profile...</div>;
  if (!student) return <div className="p-8">Student not found.</div>;

  return (
    <div className="space-y-6 max-w-6xl mx-auto">
      {/* Header */}
      <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200 flex justify-between items-start border-t-4 border-t-lgs-red">
        <div>
          <h1 className="text-2xl font-bold text-lgs-blue flex items-center gap-3">
            <User className="w-6 h-6 text-lgs-red" />
            Student STN: {student.stn}
          </h1>
          <div className="mt-2 flex gap-4 text-sm text-slate-600 items-center">
            <span className="bg-slate-100 px-2 py-1 rounded">Grade: {student.grade || 'N/A'}</span>
            <span className="bg-slate-100 px-2 py-1 rounded">Gender: {student.gender || 'N/A'}</span>
            <span className="bg-slate-100 px-2 py-1 rounded">Ethnicity: {student.ethnicity || 'N/A'}</span>
            <button onClick={() => setShowDemographics(true)} className="text-lgs-red hover:text-lgs-red-dark hover:underline text-sm font-medium">
              View All Demographics
            </button>
          </div>
        </div>
        <div className="text-right">
          <div className={`inline-flex items-center gap-2 px-3 py-1 rounded-full font-medium ${
            student.tier === 'Tier 1' ? 'bg-green-100 text-green-700' :
            student.tier === 'Tier 2' ? 'bg-yellow-100 text-yellow-700' :
            student.tier === 'Tier 3' ? 'bg-red-100 text-red-700' :
            'bg-slate-100 text-lgs-blue'
          }`}>
            Tier: {student.tier} ({student.tierStatus})
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left Column: Assessments */}
        <div className="lg:col-span-2 space-y-6">
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
              <BookOpen className="w-5 h-5 text-lgs-red" />
              Academic Overview
            </h2>
            {assessments.length === 0 ? (
              <p className="text-slate-500 text-sm">No assessment data available.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm text-left">
                  <thead className="bg-slate-50 text-slate-600 font-medium border-b border-slate-200 select-none">
                    <tr>
                      <th className="px-4 py-3 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestAssessmentSort('formattedDate')}>
                        <div className="flex items-center">Date {getAssessmentSortIcon('formattedDate')}</div>
                      </th>
                      <th className="px-4 py-3 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestAssessmentSort('type')}>
                        <div className="flex items-center">Type {getAssessmentSortIcon('type')}</div>
                      </th>
                      <th className="px-4 py-3 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestAssessmentSort('subject')}>
                        <div className="flex items-center">Subject {getAssessmentSortIcon('subject')}</div>
                      </th>
                      <th className="px-4 py-3 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestAssessmentSort('score')}>
                        <div className="flex items-center">Score {getAssessmentSortIcon('score')}</div>
                      </th>
                      <th className="px-4 py-3 cursor-pointer hover:bg-slate-100 transition-colors" onClick={() => requestAssessmentSort('proficiency')}>
                        <div className="flex items-center">Proficiency {getAssessmentSortIcon('proficiency')}</div>
                      </th>
                      <th className="px-4 py-3 text-right">Details</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {sortedAssessmentsTable.map((a) => {
                      const displayData = a.displayData;
                      return (
                        <tr key={a.id} className="hover:bg-slate-50">
                          <td className="px-4 py-3">{displayData.formattedDate}</td>
                          <td className="px-4 py-3 flex items-center gap-2">
                            {displayData.type}
                            {a.fileName && (
                              <div title={`Source File: ${a.fileName}`} className="cursor-help text-slate-400 hover:text-slate-600">
                                <Info className="w-4 h-4" />
                              </div>
                            )}
                          </td>
                          <td className="px-4 py-3">{displayData.subject}</td>
                          <td className="px-4 py-3 font-medium flex items-center gap-2">
                            {displayData.score}
                            <div title={`Source Field: ${displayData.scoreSource}`} className="cursor-help text-slate-400 hover:text-slate-600">
                              <Info className="w-4 h-4" />
                            </div>
                          </td>
                          <td className="px-4 py-3">
                            <span className={`px-2 py-1 rounded-full text-xs font-medium ${
                              displayData.proficiency.includes('Below') ? 'bg-red-100 text-red-700' :
                              displayData.proficiency.includes('Approaching') ? 'bg-yellow-100 text-yellow-700' :
                              (displayData.proficiency.includes('At ') || displayData.proficiency.includes('Above')) ? 'bg-green-100 text-green-700' :
                              'bg-slate-100 text-slate-700'
                            }`}>
                              {displayData.proficiency}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-right">
                            <button onClick={() => setSelectedAssessment(a)} className="text-lgs-blue hover:text-lgs-blue-dark p-1 rounded hover:bg-slate-100 transition-colors" title="View Full Assessment Data">
                              <FileJson className="w-4 h-4" />
                            </button>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          {/* Notes Section */}
          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
              <MessageSquare className="w-5 h-5 text-lgs-red" />
              Collaboration Notes
            </h2>
            <div className="space-y-4 mb-4 max-h-64 overflow-y-auto">
              {notes.length === 0 ? (
                <p className="text-slate-500 text-sm">No notes yet.</p>
              ) : (
                notes.map(note => (
                  <div key={note.id} className={`p-3 rounded-lg border ${note.type === 'AI Summary' ? 'bg-slate-100 border-lgs-blue-light/30' : 'bg-slate-50 border-slate-100'}`}>
                    <div className="flex justify-between items-start mb-1">
                      <span className={`font-medium text-sm ${note.type === 'AI Summary' ? 'text-lgs-blue' : 'text-slate-900'}`}>{note.authorName} <span className={`text-xs font-normal capitalize ${note.type === 'AI Summary' ? 'text-lgs-blue-light' : 'text-slate-500'}`}>({note.type})</span></span>
                      <div className="flex items-center gap-2">
                        <span className={`text-xs ${note.type === 'AI Summary' ? 'text-lgs-blue-light' : 'text-slate-500'}`}>{new Date(note.date).toLocaleDateString()}</span>
                        {role === 'admin' && (
                          <button onClick={() => setNoteToDelete(note.id)} className="text-red-500 hover:text-red-700 p-1 rounded hover:bg-red-50 transition-colors" title="Delete Note">
                            <Trash2 className="w-3 h-3" />
                          </button>
                        )}
                      </div>
                    </div>
                    <p className={`text-sm whitespace-pre-wrap ${note.type === 'AI Summary' ? 'text-slate-800' : 'text-slate-700'}`}>{note.content}</p>
                  </div>
                ))
              )}
            </div>
            <div className="flex gap-2">
              <input
                type="text"
                value={newNote}
                onChange={(e) => setNewNote(e.target.value)}
                placeholder="Add a note..."
                className="flex-1 px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none"
              />
              <button onClick={handleAddNote} className="px-4 py-2 bg-lgs-red text-white text-sm font-medium rounded-lg hover:bg-lgs-red-dark">
                Post
              </button>
            </div>
          </div>
        </div>

        {/* Right Column: Tiering & Audit */}
        <div className="space-y-6">
          {role === 'admin' && (
            <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200 border-t-4 border-t-lgs-blue">
              <h2 className="text-lg font-semibold text-lgs-blue mb-4">Tier Management</h2>
              
              <button
                onClick={generateTierRecommendation}
                disabled={isGeneratingTier || assessments.length === 0}
                className="w-full mb-6 px-4 py-2 bg-lgs-blue text-white rounded-lg text-sm font-medium hover:bg-lgs-blue-dark disabled:opacity-50 transition-colors"
              >
                {isGeneratingTier ? 'Analyzing Data...' : 'Generate AI Recommendation'}
              </button>

              <div className="pt-4 border-t border-slate-100">
                <label className="block text-sm font-medium text-slate-700 mb-2">Override / Finalize Tier</label>
                <div className="flex gap-2">
                  <select
                    value={overrideTier}
                    onChange={(e) => setOverrideTier(e.target.value)}
                    className="flex-1 px-3 py-2 border border-slate-300 rounded-lg text-sm focus:ring-2 focus:ring-lgs-blue outline-none"
                  >
                    <option value="">Select Tier...</option>
                    <option value="Tier 1">Tier 1</option>
                    <option value="Tier 2">Tier 2</option>
                    <option value="Tier 3">Tier 3</option>
                  </select>
                  <button
                    onClick={handleOverrideTier}
                    disabled={!overrideTier}
                    className="px-4 py-2 bg-lgs-red text-white text-sm font-medium rounded-lg hover:bg-lgs-red-dark disabled:opacity-50"
                  >
                    Save
                  </button>
                </div>
              </div>
            </div>
          )}

          <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
            <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
              <Clock className="w-5 h-5 text-lgs-red" />
              Audit Trail
            </h2>
            <div className="space-y-4 max-h-96 overflow-y-auto">
              {auditLogs.length === 0 ? (
                <p className="text-slate-500 text-sm">No audit history.</p>
              ) : (
                auditLogs.map(log => (
                  <div key={log.id} className="relative pl-4 border-l-2 border-slate-200 pb-4 last:pb-0">
                    <div className="absolute w-2 h-2 bg-lgs-blue rounded-full -left-[5px] top-1.5"></div>
                    <p className="text-sm font-medium text-slate-900">{log.action}</p>
                    <p className="text-xs text-slate-500 mt-0.5">{new Date(log.date).toLocaleString()}</p>
                    {log.details && <p className="text-xs text-slate-600 mt-1 bg-slate-50 p-2 rounded">{log.details}</p>}
                  </div>
                ))
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Demographics Modal */}
      {showDemographics && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-lg w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-4">Full Demographics</h3>
            <div className="space-y-3 text-sm">
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">STN</span>
                <span className="col-span-2 text-slate-900">{student.stn}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">Grade</span>
                <span className="col-span-2 text-slate-900">{student.grade || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">Gender</span>
                <span className="col-span-2 text-slate-900">{student.gender || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">Ethnicity</span>
                <span className="col-span-2 text-slate-900">{student.ethnicity || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">SPED Status</span>
                <span className="col-span-2 text-slate-900">{student.spedStatus || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">ELL Status</span>
                <span className="col-span-2 text-slate-900">{student.ellStatus || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">Section 504</span>
                <span className="col-span-2 text-slate-900">{student.section504 || 'N/A'}</span>
              </div>
              <div className="grid grid-cols-3 border-b border-slate-100 pb-2">
                <span className="text-slate-500 font-medium">Last Updated</span>
                <span className="col-span-2 text-slate-900">{student.lastUpdated ? new Date(student.lastUpdated).toLocaleString() : 'N/A'}</span>
              </div>
            </div>
            <div className="mt-6 flex justify-end">
              <button onClick={() => setShowDemographics(false)} className="px-4 py-2 bg-slate-100 text-slate-700 font-medium hover:bg-slate-200 rounded-lg transition-colors">
                Close
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Assessment Details Modal */}
      {selectedAssessment && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-2xl w-full p-6 max-h-[90vh] flex flex-col">
            <h3 className="text-lg font-bold text-slate-900 mb-4">Assessment Details</h3>
            <div className="overflow-y-auto flex-1 space-y-4 text-sm">
              <div className="grid grid-cols-2 gap-4 bg-slate-50 p-4 rounded-lg border border-slate-100">
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-1">Type</span>
                  <span className="text-slate-900">{getAssessmentDisplayData(selectedAssessment).type}</span>
                </div>
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-1">Subject</span>
                  <span className="text-slate-900">{getAssessmentDisplayData(selectedAssessment).subject}</span>
                </div>
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-1">Score</span>
                  <span className="text-slate-900">{getAssessmentDisplayData(selectedAssessment).score}</span>
                </div>
                <div>
                  <span className="block text-xs text-slate-500 font-medium mb-1">Proficiency</span>
                  <span className="text-slate-900">{getAssessmentDisplayData(selectedAssessment).proficiency}</span>
                </div>
                <div className="col-span-2">
                  <span className="block text-xs text-slate-500 font-medium mb-1">Source File</span>
                  <span className="text-slate-900">{selectedAssessment.fileName || 'Unknown'}</span>
                </div>
              </div>
              
              <div>
                <h4 className="font-medium text-slate-900 mb-2">Raw Data</h4>
                <div className="bg-slate-900 text-slate-50 p-4 rounded-lg overflow-x-auto font-mono text-xs">
                  <pre>{JSON.stringify(JSON.parse(selectedAssessment.details || '{}'), null, 2)}</pre>
                </div>
              </div>
            </div>
            <div className="mt-6 flex justify-end pt-4 border-t border-slate-100">
              <button onClick={() => setSelectedAssessment(null)} className="px-4 py-2 bg-slate-100 text-slate-700 font-medium hover:bg-slate-200 rounded-lg transition-colors">
                Close
              </button>
            </div>
          </div>
        </div>
      )}
      {/* Delete Note Modal */}
      {noteToDelete && (
        <div className="fixed inset-0 bg-slate-900/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-xl shadow-lg max-w-sm w-full p-6">
            <h3 className="text-lg font-bold text-slate-900 mb-2">Delete Note</h3>
            <p className="text-sm text-slate-600 mb-6">Are you sure you want to delete this note? This action cannot be undone.</p>
            <div className="flex justify-end gap-3">
              <button onClick={() => setNoteToDelete(null)} className="px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 rounded-lg transition-colors">
                Cancel
              </button>
              <button onClick={confirmDeleteNote} className="px-4 py-2 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-lg transition-colors">
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
