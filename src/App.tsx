import React from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './contexts/AuthContext';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import StudentsList from './pages/StudentsList';
import StudentProfile from './pages/StudentProfile';
import DataIngestion from './pages/DataIngestion';
import ExportPage from './pages/ExportPage';

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { user, loading } = useAuth();
  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-lgs-bg">
        <div className="text-slate-500 text-sm">Loading…</div>
      </div>
    );
  }
  if (!user) return <Navigate to="/" replace />;
  return <>{children}</>;
}

function AppRoutes() {
  return (
    <Routes>
      <Route path="/" element={<Layout />}>
        <Route index element={<Dashboard />} />
        <Route path="students" element={<ProtectedRoute><StudentsList /></ProtectedRoute>} />
        <Route path="students/:id" element={<ProtectedRoute><StudentProfile /></ProtectedRoute>} />
        <Route path="upload" element={<ProtectedRoute><DataIngestion /></ProtectedRoute>} />
        <Route path="export" element={<ProtectedRoute><ExportPage /></ProtectedRoute>} />
      </Route>
    </Routes>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <AppRoutes />
      </AuthProvider>
    </BrowserRouter>
  );
}
