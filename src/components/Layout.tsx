import React from 'react';
import { Link, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { Users, Upload, LogOut, LayoutDashboard } from 'lucide-react';
import { cn } from '../lib/utils';

export default function Layout() {
  const { user, role, logOut, signIn } = useAuth();
  const location = useLocation();

  if (!user) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-lgs-bg">
        <div className="max-w-md w-full bg-white rounded-xl shadow-sm border border-slate-100 overflow-hidden">
          <div className="bg-lgs-blue p-8 flex flex-col items-center justify-center text-center">
            <img src="/logo.png" alt="Liberty Grove Schools" className="h-32 object-contain mb-4" onError={(e) => { e.currentTarget.style.display = 'none'; }} />
          </div>
          <div className="p-8 text-center">
            <p className="text-slate-500 mb-8">Sign in to access the platform</p>
            <button
              onClick={() => signIn()}
              className="w-full bg-lgs-red text-white py-2 px-4 rounded-lg font-medium hover:bg-lgs-red-dark transition-colors"
            >
              Sign In with Google
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
