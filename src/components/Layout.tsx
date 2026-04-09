import React, { useState } from 'react';
import { Link, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { Users, Upload, LogOut, LayoutDashboard } from 'lucide-react';
import { cn } from '../lib/utils';

export default function Layout() {
  const { user, role, logOut, signIn, signInWithEmail, signUpWithEmail } = useAuth();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [isCreating, setIsCreating] = useState(false);

  const handleEmailLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      const email = username.includes('@') ? username : `${username}@lgs.local`;
      await signInWithEmail(email, password);
    } catch (err: any) {
      if (err.code === 'auth/invalid-credential' || err.code === 'auth/user-not-found') {
        setError('Invalid username or password');
      } else {
        setError(err.message);
      }
    }
  };

  const handleCreateAdmin = async () => {
    setIsCreating(true);
    setError('');
    try {
      await signUpWithEmail('LGSAdmin@lgs.local', 'Ch@ng3P@@5w00rd', 'admin');
      alert('Admin user created successfully! You can now log in.');
    } catch (err: any) {
      setError(err.message);
    }
    setIsCreating(false);
  };

  if (!user) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-lgs-bg">
        <div className="max-w-md w-full bg-white rounded-xl shadow-sm border border-slate-100 overflow-hidden">
          <div className="bg-lgs-blue p-8 flex flex-col items-center justify-center text-center">
            <img src="/logo.png" alt="Liberty Grove Schools" className="h-32 object-contain mb-4" onError={(e) => { e.currentTarget.style.display = 'none'; }} />
          </div>
          <div className="p-8">
            <p className="text-slate-500 mb-6 text-center">Sign in to access the platform</p>
            
            {error && <div className="mb-4 p-3 bg-red-50 text-red-600 text-sm rounded-lg">{error}</div>}
            
            <form onSubmit={handleEmailLogin} className="space-y-4 mb-6">
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Username or Email</label>
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-lgs-blue"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-1">Password</label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="w-full px-3 py-2 border border-slate-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-lgs-blue"
                  required
                />
              </div>
              <button
                type="submit"
                className="w-full bg-lgs-blue text-white py-2 px-4 rounded-lg font-medium hover:bg-lgs-blue-dark transition-colors"
              >
                Sign In
              </button>
            </form>

            <div className="relative mb-6">
              <div className="absolute inset-0 flex items-center">
                <div className="w-full border-t border-slate-200"></div>
              </div>
              <div className="relative flex justify-center text-sm">
                <span className="px-2 bg-white text-slate-500">Or continue with</span>
              </div>
            </div>

            <button
              onClick={() => signIn()}
              className="w-full bg-white border border-slate-300 text-slate-700 py-2 px-4 rounded-lg font-medium hover:bg-slate-50 transition-colors flex items-center justify-center gap-2 mb-4"
            >
              <svg className="w-5 h-5" viewBox="0 0 24 24">
                <path fill="currentColor" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" />
                <path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" />
                <path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" />
                <path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" />
              </svg>
              Google
            </button>
            
            <button
              onClick={handleCreateAdmin}
              disabled={isCreating}
              className="w-full text-sm text-slate-500 hover:text-lgs-blue underline"
            >
              {isCreating ? 'Creating...' : 'Create LGSAdmin User'}
            </button>
          </div>
        </div>
      </div>
    );
  }

  const navItems = [
    { name: 'Dashboard', path: '/', icon: LayoutDashboard },
    { name: 'Students', path: '/students', icon: Users },
  ];

  if (role === 'admin') {
    navItems.push({ name: 'Data Ingestion', path: '/upload', icon: Upload });
  }

  return (
    <div className="min-h-screen bg-lgs-bg flex">
      {/* Sidebar */}
      <aside className="w-64 bg-lgs-blue text-white flex flex-col shadow-xl z-10">
        <div className="p-6 border-b border-lgs-blue-dark">
          <div className="flex items-center justify-center mb-4">
            <img src="/logo.png" alt="Liberty Grove Schools" className="w-full h-auto object-contain" onError={(e) => { e.currentTarget.style.display = 'none'; }} />
          </div>
        </div>
        
        <nav className="flex-1 py-4 space-y-1">
          {navItems.map((item) => {
            const Icon = item.icon;
            const isActive = location.pathname === item.path || (item.path !== '/' && location.pathname.startsWith(item.path));
            return (
               <Link
                key={item.name}
                to={item.path}
                className={cn(
                  "flex items-center gap-3 px-6 py-3 text-sm font-medium transition-all border-l-4",
                  isActive 
                    ? "bg-lgs-blue-dark text-white border-lgs-red" 
                    : "text-slate-300 hover:bg-lgs-blue-dark hover:text-white border-transparent"
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
            <div className="w-8 h-8 rounded-full bg-lgs-blue-light flex items-center justify-center text-white font-bold">
              {user.email?.[0].toUpperCase()}
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-white truncate">{user.displayName || user.email}</p>
              <p className="text-xs text-lgs-blue-light capitalize">{role}</p>
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
