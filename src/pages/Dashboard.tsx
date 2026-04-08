import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Search, User, AlertCircle } from 'lucide-react';
import { collection, query, where, getDocs } from 'firebase/firestore';
import { db } from '../firebase';
import { handleFirestoreError, OperationType } from '../lib/utils';

export default function Dashboard() {
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [error, setError] = useState('');
  const navigate = useNavigate();

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

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-bold text-lgs-blue">Dashboard</h1>
        <p className="text-slate-500 mt-1">Welcome to the Liberty Grove Impact Platform</p>
      </div>

      <div className="bg-white p-8 rounded-xl shadow-sm border border-slate-200 max-w-2xl">
        <h2 className="text-lg font-semibold text-lgs-blue mb-4 flex items-center gap-2">
          <Search className="w-5 h-5 text-lgs-red" />
          Student Search
        </h2>
        <form onSubmit={handleSearch} className="flex gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Enter Student Test Number (STN)"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full px-4 py-2 border border-slate-300 rounded-lg focus:ring-2 focus:ring-lgs-blue focus:border-lgs-blue outline-none transition-all"
            />
          </div>
          <button
            type="submit"
            disabled={isSearching || !searchQuery.trim()}
            className="px-6 py-2 bg-lgs-red text-white rounded-lg font-medium hover:bg-lgs-red-dark disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {isSearching ? 'Searching...' : 'Search'}
          </button>
        </form>
        {error && (
          <div className="mt-4 p-3 bg-red-50 text-red-700 rounded-lg flex items-center gap-2 text-sm">
            <AlertCircle className="w-4 h-4" />
            {error}
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
        <div className="bg-white p-6 rounded-xl shadow-sm border border-slate-200">
          <div className="w-10 h-10 rounded-full bg-slate-100 flex items-center justify-center mb-4">
            <User className="w-5 h-5 text-lgs-blue" />
          </div>
          <h3 className="font-semibold text-lgs-blue">Quick View</h3>
          <p className="text-sm text-slate-500 mt-1">Search for a student by STN to view their core identifiers, grade level, and current intervention status.</p>
        </div>
      </div>
    </div>
  );
}
