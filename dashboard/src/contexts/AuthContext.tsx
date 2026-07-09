import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import type { User, UserRole } from '../types';

interface AuthContextType {
  user: User | null;
  login: (username: string, password: string) => Promise<{ success: boolean; error?: string }>;
  logout: () => void;
  isAdmin: boolean;
  isUser: boolean;
  isLoading: boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

const TOKEN_KEY = 'plc_gateway_token';
const USER_KEY = 'plc_gateway_user';

// Decodes the role/name claims out of the backend JWT payload.
function userFromToken(token: string, loginId: string): User {
  const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  const payload = JSON.parse(window.atob(base64));
  const role = (
    payload.role ||
    payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ||
    'user'
  ).toLowerCase() as UserRole;
  const name =
    payload.unique_name ||
    payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'] ||
    loginId;
  return { id: payload.jti || '1', username: loginId, email: loginId, name, role };
}

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const clearSession = () => {
    setUser(null);
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
  };

  // Restore an existing session from the stored token/user.
  useEffect(() => {
    const storedToken = localStorage.getItem(TOKEN_KEY);
    const storedUser = localStorage.getItem(USER_KEY);
    if (storedToken && storedUser) {
      try {
        setUser(JSON.parse(storedUser) as User);
      } catch {
        clearSession();
      }
    }
    setIsLoading(false);
  }, []);

  // Authenticates against the backend JWT endpoint only. There is no local/offline login.
  const login = async (username: string, password: string): Promise<{ success: boolean; error?: string }> => {
    setIsLoading(true);
    try {
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      });

      if (!response.ok) {
        let message = 'Invalid username or password';
        try {
          const err = await response.json();
          if (err?.message) message = err.message;
        } catch { /* non-JSON error body */ }
        return { success: false, error: message };
      }

      const { token } = await response.json();
      const authUser = userFromToken(token, username.trim());

      setUser(authUser);
      localStorage.setItem(TOKEN_KEY, token);
      localStorage.setItem(USER_KEY, JSON.stringify(authUser));
      return { success: true };
    } catch (err) {
      console.error('Login error:', err);
      return { success: false, error: 'Connection to the server failed' };
    } finally {
      setIsLoading(false);
    }
  };

  const logout = () => clearSession();

  const value: AuthContextType = {
    user,
    login,
    logout,
    isAdmin: user?.role === 'admin',
    isUser: user?.role === 'user',
    isLoading,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};

export const useAuth = (): AuthContextType => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
};
