import React, { createContext, useContext, useEffect, useState } from 'react';
import { User, onAuthStateChanged, signInWithPopup, GoogleAuthProvider, signOut } from 'firebase/auth';
import { doc, getDoc, setDoc } from 'firebase/firestore';
import { auth, db } from '../firebase';
import { handleFirestoreError, OperationType } from '../lib/utils';

interface AuthContextType {
  user: User | null;
  role: 'admin' | 'teacher' | null;
  loading: boolean;
  signIn: () => Promise<void>;
  logOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType>({
  user: null,
  role: null,
  loading: true,
  signIn: async () => {},
  logOut: async () => {},
});

export const useAuth = () => useContext(AuthContext);

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<User | null>(null);
  const [role, setRole] = useState<'admin' | 'teacher' | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const unsubscribe = onAuthStateChanged(auth, async (currentUser) => {
      setUser(currentUser);
      if (currentUser) {
        try {
          const userDocRef = doc(db, 'users', currentUser.uid);
          let userDoc;
          try {
            userDoc = await getDoc(userDocRef);
          } catch (e) {
            handleFirestoreError(e, OperationType.GET, 'users');
            return;
          }
          
          if (userDoc.exists()) {
            const isDefaultAdmin = currentUser.email === 'bunzueta@gmail.com' && currentUser.emailVerified;
            setRole(isDefaultAdmin ? 'admin' : userDoc.data().role);
          } else {
            // Default to teacher if not found, or admin if it's the default admin email
            const isDefaultAdmin = currentUser.email === 'bunzueta@gmail.com' && currentUser.emailVerified;
            const newRole = isDefaultAdmin ? 'admin' : 'teacher';
            try {
              await setDoc(userDocRef, {
                email: currentUser.email,
                role: newRole,
                name: currentUser.displayName || ''
              });
            } catch (e) {
              handleFirestoreError(e, OperationType.WRITE, 'users');
              return;
            }
            setRole(newRole);
          }
        } catch (error) {
          console.error("Unexpected error in AuthContext", error);
        }
      } else {
        setRole(null);
      }
      setLoading(false);
    });

    return () => unsubscribe();
  }, []);

  const signIn = async () => {
    const provider = new GoogleAuthProvider();
    try {
      await signInWithPopup(auth, provider);
    } catch (error) {
      console.error("Error signing in", error);
    }
  };

  const logOut = async () => {
    await signOut(auth);
  };

  return (
    <AuthContext.Provider value={{ user, role, loading, signIn, logOut }}>
      {children}
    </AuthContext.Provider>
  );
};
