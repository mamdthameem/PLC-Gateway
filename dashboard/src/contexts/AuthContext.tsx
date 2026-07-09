import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import type { User, UserRole } from '../types';
import { dataService } from '../services/dataService';
import { isUserExpired } from '../utils/formatters';

interface AuthContextType {
  user: User | null;
  login: (username: string, password: string) => Promise<{ success: boolean; error?: string }>;
  logout: () => void;
  isAdmin: boolean;
  isUser: boolean;
  isLoading: boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const clearSession = () => {
    setUser(null);
    localStorage.removeItem('plc_gateway_token');
    localStorage.removeItem('plc_gateway_user');
  };

  useEffect(() => {
    // Check for stored session
    const storedToken = localStorage.getItem('plc_gateway_token');
    const storedUser = localStorage.getItem('plc_gateway_user');
    if (storedToken && storedUser) {
      try {
        const parsed = JSON.parse(storedUser) as User & { createdAt?: string; validUntil?: string };
        const hydratedUser: User = {
          ...parsed,
          createdAt: parsed.createdAt ? new Date(parsed.createdAt) : new Date(),
          validUntil: parsed.validUntil ? new Date(parsed.validUntil) : undefined,
        };

        const isExpired = isUserExpired(hydratedUser.validUntil);
        if (hydratedUser.isApproved === false || isExpired) {
          clearSession();
        } else {
          setUser(hydratedUser);
        }
      } catch (error) {
        console.error('Failed to parse stored user', error);
        clearSession();
      }
    }
    setIsLoading(false);
  }, []);

  useEffect(() => {
    const validUntil = user?.validUntil;
    if (!validUntil) return;

    const checkValidity = () => {
      if (isUserExpired(validUntil)) {
        clearSession();
      }
    };

    checkValidity();
    const interval = setInterval(checkValidity, 60 * 1000);
    return () => clearInterval(interval);
  }, [user?.id, user?.validUntil]);

  const login = async (username: string, password: string): Promise<{ success: boolean; error?: string }> => {
    setIsLoading(true);

    const tryLocalLogin = (): { success: boolean; error?: string; matched?: boolean } => {
      const normalized = username.trim().toLowerCase();
      const users = dataService.getUsers();
      const matchedUser = users.find(u => {
        const localUser = u as User & { username?: string };
        const candidates = [
          localUser.username,
          localUser.email,
          localUser.name,
        ].filter(Boolean).map(v => String(v).toLowerCase());
        return candidates.includes(normalized);
      });

      if (!matchedUser) {
        return { success: false, error: 'Invalid username or password', matched: false };
      }

      if (matchedUser.isApproved === false) {
        const reason = (matchedUser.unapprovedReason || '').trim();
        return {
          success: false,
          error: reason ? `Access not approved: ${reason}` : 'User is not approved for access',
          matched: true,
        };
      }

      if (isUserExpired(matchedUser.validUntil)) {
        return { success: false, error: 'Your access has expired. Please contact your administrator.', matched: true };
      }

      if (!matchedUser.password || matchedUser.password !== password) {
        return { success: false, error: 'Invalid username or password', matched: true };
      }

      setUser(matchedUser);
      localStorage.setItem('plc_gateway_token', `local-${matchedUser.id}`);
      localStorage.setItem('plc_gateway_user', JSON.stringify(matchedUser));
      setIsLoading(false);
      return { success: true, matched: true };
    };

    try {
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      });

      if (!response.ok) {
        // If API login fails, try local users (admin-created users)
        const localResult = tryLocalLogin();
        if (localResult.matched) {
          return { success: localResult.success, error: localResult.error };
        }

        const errorData = await response.json();
        setIsLoading(false);
        return { success: false, error: errorData.message || 'Login failed' };
      }

      const { token } = await response.json();

      // Basic JWT decoding
      const base64Url = token.split('.')[1];
      const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
      const payload = JSON.parse(window.atob(base64));

      const authUser: User = {
        id: payload.jti || '1',
        email: username,
        name: payload.unique_name || username,
        role: (payload.role || payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] || "user").toLowerCase() as UserRole,
        isApproved: true,
        assignedMachineIds: [],
        createdAt: new Date(),
        customerId: payload.tenant_id,
      };

      setUser(authUser);
      localStorage.setItem('plc_gateway_token', token);
      localStorage.setItem('plc_gateway_user', JSON.stringify(authUser));
      setIsLoading(false);
      return { success: true };
    } catch (err) {
      console.error('Login error:', err);
      const localResult = tryLocalLogin();
      if (localResult.matched) {
        return { success: localResult.success, error: localResult.error };
      }
      setIsLoading(false);
      return { success: false, error: 'Connection to authentication server failed' };
    }
  };

  const logout = () => {
    clearSession();
  };

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
