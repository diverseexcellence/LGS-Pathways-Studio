import React, { createContext, useContext, useState, useEffect, useCallback } from 'react';

interface AdminUser {
  adminId: number;
  email: string;
  name: string;
  role: 'admin';
}

interface AuthContextType {
  user: AdminUser | null;
  role: 'admin' | null;
  loading: boolean;
  token: string | null;
  signInWithEmail: (email: string, password: string) => Promise<void>;
  logOut: () => void;
}

const AuthContext = createContext<AuthContextType>({
  user: null,
  role: null,
  loading: true,
  token: null,
  signInWithEmail: async () => {},
  logOut: () => {},
});

export const useAuth = () => useContext(AuthContext);

// Token stored in module-level variable (memory only — not localStorage for security)
let memoryToken: string | null = null;
let inactivityTimer: ReturnType<typeof setTimeout> | null = null;
const INACTIVITY_TIMEOUT_MS = 60 * 60 * 1000; // 60 minutes per PRD US-02

function parseJwt(token: string): { exp: number; adminId: number; email: string; name: string } | null {
  try {
    const payload = token.split('.')[1];
    return JSON.parse(atob(payload));
  } catch {
    return null;
  }
}

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<AdminUser | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const clearSession = useCallback(() => {
    memoryToken = null;
    setToken(null);
    setUser(null);
    if (inactivityTimer) clearTimeout(inactivityTimer);
  }, []);

  const resetInactivityTimer = useCallback(() => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    inactivityTimer = setTimeout(clearSession, INACTIVITY_TIMEOUT_MS);
  }, [clearSession]);

  // Restore session from sessionStorage on mount (survives page refresh but not tab close)
  useEffect(() => {
    const savedToken = sessionStorage.getItem('lgs_token');
    if (savedToken) {
      const claims = parseJwt(savedToken);
      if (claims && claims.exp * 1000 > Date.now()) {
        memoryToken = savedToken;
        setToken(savedToken);
        setUser({ adminId: claims.adminId, email: claims.email, name: claims.name, role: 'admin' });
        resetInactivityTimer();
      } else {
        sessionStorage.removeItem('lgs_token');
      }
    }
    setLoading(false);
  }, [resetInactivityTimer]);

  // Reset inactivity timer on any user interaction
  useEffect(() => {
    if (!user) return;
    const events = ['mousedown', 'keydown', 'scroll', 'touchstart'];
    events.forEach(e => window.addEventListener(e, resetInactivityTimer));
    return () => events.forEach(e => window.removeEventListener(e, resetInactivityTimer));
  }, [user, resetInactivityTimer]);

  const signInWithEmail = async (email: string, password: string) => {
    const apiBase = import.meta.env.VITE_API_URL || 'http://localhost:5000';
    const res = await fetch(`${apiBase}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });

    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.message || 'Invalid credentials');
    }

    const data: { token: string; adminId: number; email: string; name: string } = await res.json();
    memoryToken = data.token;
    sessionStorage.setItem('lgs_token', data.token);
    setToken(data.token);
    setUser({ adminId: data.adminId, email: data.email, name: data.name, role: 'admin' });
    resetInactivityTimer();
  };

  const logOut = useCallback(() => {
    sessionStorage.removeItem('lgs_token');
    clearSession();
  }, [clearSession]);

  return (
    <AuthContext.Provider value={{ user, role: user ? 'admin' : null, loading, token, signInWithEmail, logOut }}>
      {children}
    </AuthContext.Provider>
  );
};

// Exported for use in API service without React context
export const getMemoryToken = () => memoryToken;
