import React, { useState } from 'react';
import { Link, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { Users, Upload, LogOut, LayoutDashboard, FileDown } from 'lucide-react';
import { cn } from '../lib/utils';

export default function Layout() {
  const { user, role, logOut, signInWithEmail } = useAuth();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [isSigningIn, setIsSigningIn] = useState(false);

  const handleEmailLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setIsSigningIn(true);
    try {
      const email = username.includes('@') ? username : `${username}@lgs.local`;
      await signInWithEmail(email, password);
    } catch (err: any) {
      setError(err.message || 'Invalid username or password');
    } finally {
      setIsSigningIn(false);
    }
  };

  if (!user) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-lgs-bg">
        <div className="max-w-md w-full bg-white rounded-xl shadow-sm border border-slate-100 overflow-hidden">
          <div className="bg-lgs-blue p-8 flex flex-col items-center justify-center text-center">
            <img
              src="/logo.png"
              alt="LGS Impact"
              className="h-32 object-contain mb-4"
              onError={(e) => { e.currentTarget.style.display = 'none'; }}
            />
            <h1 className="text-white text-xl font-bold tracking-wide">LGS Impact</h1>
            <p className="text-slate-300 text-sm mt-1">Student Data Platform</p>
          </div>
          <div className="p-8">
            <p className="text-slate-500 mb-6 text-center text-sm">Sign in to access the platform</p>

            {error && (
              <div className="mb-4 p-3 bg-red-50 text-red-600 text-sm rounded-lg border border-red-200">
                {error}
              </div>
            )}

            <form onSubmit={handleEmailLogin} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">
                  Username or Email
                </label>
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-lgs-blue text-sm"
                  placeholder="velvet or velvet@lgs.local"
                  required
                  autoComplete="username"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Password</label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-lgs-blue text-sm"
                  required
                  autoComplete="current-password"
                />
              </div>
              <button
                type="submit"
                disabled={isSigningIn}
                className="w-full bg-lgs-blue text-white py-2 px-4 rounded-lg font-medium hover:bg-lgs-blue-dark transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
              >
                {isSigningIn ? 'Signing in…' : 'Sign In'}
              </button>
            </form>
          </div>
        </div>
      </div>
    );
  }

  const navItems = [
    { name: 'Dashboard', path: '/', icon: LayoutDashboard },
    { name: 'Students', path: '/students', icon: Users },
    { name: 'Data Upload', path: '/upload', icon: Upload },
  ];

  return (
    <div className="min-h-screen bg-lgs-bg flex">
      {/* Sidebar */}
      <aside className="w-64 bg-lgs-blue text-white flex flex-col shadow-xl z-10">
        <div className="p-6 border-b border-lgs-blue-dark">
          <div className="flex items-center justify-center mb-2">
            <img
              src="/logo.png"
              alt="LGS Impact"
              className="w-full h-auto object-contain"
              onError={(e) => { e.currentTarget.style.display = 'none'; }}
            />
          </div>
          <p className="text-center text-xs text-slate-400 mt-1 tracking-widest uppercase">
            Impact Platform
          </p>
        </div>

        <nav className="flex-1 py-4 space-y-1">
          {navItems.map((item) => {
            const Icon = item.icon;
            const isActive =
              location.pathname === item.path ||
              (item.path !== '/' && location.pathname.startsWith(item.path));
            return (
              <Link
                key={item.name}
                to={item.path}
                className={cn(
                  'flex items-center gap-3 px-6 py-3 text-sm font-medium transition-all border-l-4',
                  isActive
                    ? 'bg-lgs-blue-dark text-white border-lgs-red'
                    : 'text-slate-300 hover:bg-lgs-blue-dark hover:text-white border-transparent'
                )}
              >
                <Icon className="w-5 h-5" />
                {item.name}
              </Link>
            );
          })}
        </nav>

        <div className="p-4 border-t border-lgs-blue-dark bg-lgs-blue-dark/30">
          <div className="flex items-center gap-3 mb-4 px-2">
            <div className="w-8 h-8 rounded-full bg-lgs-red flex items-center justify-center text-white font-bold text-sm">
              {user.name?.[0]?.toUpperCase() || user.email[0].toUpperCase()}
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-white truncate">{user.name || user.email}</p>
              <p className="text-xs text-slate-400 capitalize">{role}</p>
            </div>
          </div>
          <button
            onClick={logOut}
            className="flex items-center gap-2 w-full px-4 py-2 text-sm font-medium text-slate-300 rounded-md hover:bg-lgs-red hover:text-white transition-colors"
          >
            <LogOut className="w-4 h-4" />
            Sign Out
          </button>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 overflow-auto">
        <div className="p-8 max-w-7xl mx-auto">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
