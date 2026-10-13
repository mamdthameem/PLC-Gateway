import React, { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { initializeApp, getApps, type FirebaseApp } from 'firebase/app';
import {
  getAuth, onAuthStateChanged, sendPasswordResetEmail, signInWithEmailAndPassword, signOut, type Auth,
} from 'firebase/auth';
import type { LoginKind, User, UserRole } from '../types';
import { API_BASE } from '../services/apiBase';
import { apiFetch, reasonOf, setSessionEndedHandler, setTokenSource } from '../services/apiClient';

/**
 * Who is signed in to this dashboard, and how (see GatewayAuth on the gateway).
 *
 * - **Email** (the normal way): Firebase Authentication, one account per person, managed from the
 *   cloud. The browser signs in with Firebase; every API call then carries a fresh Firebase ID token
 *   (apiClient asks for one each time), which the gateway checks itself.
 * - **Shared login** (old, being retired): the users-table username/password; the gateway signs the
 *   token. Offered only while the gateway has it switched on.
 * - **Local admin** (emergency, internet down): one account, plant network only; the gateway signs
 *   the token. Offered only when the gateway says this browser is on the plant network.
 *
 * After any sign-in the gateway is asked who it sees (/api/auth/me): that is where a person from
 * another client is turned away, with the gateway's own reason.
 */

/** GET /api/auth/config — what the login page offers. */
export interface LoginOptions {
  firebase: { apiKey: string; authDomain: string; projectId: string; appId: string } | null;
  emailLogin: boolean;
  legacyLogin: boolean;
  localAdmin: boolean;
}

type Result = { success: boolean; error?: string };

interface AuthContextType {
  user: User | null;
  /** null while loading, or when the gateway could not be reached. */
  options: LoginOptions | null;
  optionsError: string | null;
  isLoading: boolean;
  /** Why the last session ended (expired, refused, signed out elsewhere) — shown on the login page. */
  sessionMessage: string | null;
  loginWithEmail: (email: string, password: string) => Promise<Result>;
  loginShared: (username: string, password: string) => Promise<Result>;
  loginLocalAdmin: (username: string, password: string) => Promise<Result>;
  sendPasswordReset: (email: string) => Promise<Result>;
  logout: () => void;
  isAdmin: boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/** The gateway-signed token (shared login or local admin). Email sign-ins keep nothing here. */
const TOKEN_KEY = 'plc_gateway_token';

let firebaseAuth: Auth | null = null;

function startFirebase(config: NonNullable<LoginOptions['firebase']>): Auth {
  if (!firebaseAuth) {
    const app: FirebaseApp = getApps()[0] ?? initializeApp(config);
    firebaseAuth = getAuth(app);
  }
  return firebaseAuth;
}

function readStoredToken(): string | null {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

function storeToken(token: string | null): void {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* storage blocked: the session just will not survive a refresh */ }
}

// Every API call asks for a token here: the gateway-signed one if this tab has it, otherwise a
// fresh Firebase ID token (getIdToken renews it shortly before it expires).
setTokenSource(async () => {
  const stored = readStoredToken();
  if (stored) return stored;
  return firebaseAuth?.currentUser ? firebaseAuth.currentUser.getIdToken() : null;
});

const FIREBASE_ERRORS: Record<string, string> = {
  'auth/invalid-credential': 'Wrong email or password.',
  'auth/wrong-password': 'Wrong email or password.',
  'auth/user-not-found': 'Wrong email or password.',
  'auth/invalid-email': 'That does not look like an email address.',
  'auth/user-disabled': 'This account has been disabled. Ask your administrator.',
  'auth/too-many-requests': 'Too many attempts. Wait a few minutes, or use "Forgot password?".',
  'auth/network-request-failed':
    'Cannot reach the sign-in service. If the internet is down here, use the local admin login on the plant network.',
};

function firebaseError(err: unknown): string {
  const code = (err as { code?: string }).code ?? '';
  return FIREBASE_ERRORS[code] ?? `Sign-in failed${code ? ` (${code})` : ''}.`;
}

/** Asks the gateway who it sees for the current token. */
async function whoAmI(): Promise<{ user: User } | { error: string }> {
  try {
    const res = await apiFetch('/api/auth/me');
    if (!res.ok) return { error: await reasonOf(res) };
    const me = await res.json() as { name: string; email: string; role: string; login: string };
    const role: UserRole = me.role === 'admin' ? 'admin' : 'viewer';
    return {
      user: {
        id: me.email || me.name,
        username: me.email || me.name,
        email: me.email,
        name: me.name || me.email,
        role,
        login: me.login as LoginKind,
      },
    };
  } catch {
    return { error: 'Cannot reach the gateway.' };
  }
}

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<User | null>(null);
  const [options, setOptions] = useState<LoginOptions | null>(null);
  const [optionsError, setOptionsError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [sessionMessage, setSessionMessage] = useState<string | null>(null);
  // Set while a sign-in from the login page is checking itself with the gateway, so the Firebase
  // listener below does not run the same check a second time.
  const signingIn = useRef(false);

  const endSession = useCallback((message: string | null) => {
    storeToken(null);
    if (firebaseAuth?.currentUser) void signOut(firebaseAuth);
    setUser(null);
    setSessionMessage(message);
  }, []);

  // A 401/403 on any API call ends the session, with the gateway's reason.
  useEffect(() => {
    setSessionEndedHandler(message => {
      if (readStoredToken() || firebaseAuth?.currentUser) endSession(message);
    });
  }, [endSession]);

  // Start: learn the login options, then restore whichever session this browser still has.
  useEffect(() => {
    let active = true;
    let unsubscribe: (() => void) | undefined;

    (async () => {
      let opts: LoginOptions;
      try {
        const res = await fetch(`${API_BASE}/api/auth/config`);
        if (!res.ok) throw new Error(String(res.status));
        opts = await res.json() as LoginOptions;
      } catch {
        if (active) { setOptionsError('Cannot reach the gateway. Check the connection and refresh the page.'); setIsLoading(false); }
        return;
      }
      if (!active) return;
      setOptions(opts);

      // A gateway-signed session (shared login or local admin) left in this browser.
      if (readStoredToken()) {
        const me = await whoAmI();
        if (!active) return;
        if ('user' in me) setUser(me.user);
        else endSession(me.error);
      }

      if (!opts.firebase) { setIsLoading(false); return; }
      const auth = startFirebase(opts.firebase);
      unsubscribe = onAuthStateChanged(auth, async fbUser => {
        if (!active) return;
        if (fbUser && !readStoredToken() && !signingIn.current) {
          const me = await whoAmI();
          if (!active) return;
          if ('user' in me) setUser(me.user);
          else endSession(me.error);
        }
        setIsLoading(false);
      });
    })();

    return () => { active = false; unsubscribe?.(); };
  }, [endSession]);

  const loginWithEmail = async (email: string, password: string): Promise<Result> => {
    if (!options?.firebase) return { success: false, error: 'Email sign-in is not available on this gateway.' };
    const auth = startFirebase(options.firebase);
    storeToken(null);
    signingIn.current = true;
    try {
      await signInWithEmailAndPassword(auth, email.trim(), password);
    } catch (err) {
      signingIn.current = false;
      return { success: false, error: firebaseError(err) };
    }
    const me = await whoAmI();
    signingIn.current = false;
    if ('error' in me) {
      await signOut(auth);
      return { success: false, error: me.error };
    }
    setSessionMessage(null);
    setUser(me.user);
    return { success: true };
  };

  /** The shared login and the local admin both get a gateway-signed token the same way. */
  const loginWithGateway = async (path: string, username: string, password: string): Promise<Result> => {
    try {
      const res = await fetch(`${API_BASE}${path}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: username.trim(), password }),
      });
      if (!res.ok) {
        // 404: the local admin login, asked for from outside the plant network (or not set up).
        if (res.status === 404) return { success: false, error: 'This login is not available here.' };
        return { success: false, error: await reasonOf(res) };
      }
      const { token } = await res.json() as { token: string };
      if (firebaseAuth?.currentUser) await signOut(firebaseAuth);
      storeToken(token);
      const me = await whoAmI();
      if ('error' in me) { storeToken(null); return { success: false, error: me.error }; }
      setSessionMessage(null);
      setUser(me.user);
      return { success: true };
    } catch {
      return { success: false, error: 'Cannot reach the gateway.' };
    }
  };

  const sendPasswordReset = async (email: string): Promise<Result> => {
    if (!options?.firebase) return { success: false, error: 'Email sign-in is not available on this gateway.' };
    if (!email.trim()) return { success: false, error: 'Type your email address first.' };
    try {
      await sendPasswordResetEmail(startFirebase(options.firebase), email.trim());
      return { success: true };
    } catch (err) {
      return { success: false, error: firebaseError(err) };
    }
  };

  const value: AuthContextType = {
    user,
    options,
    optionsError,
    isLoading,
    sessionMessage,
    loginWithEmail,
    loginShared: (u, p) => loginWithGateway('/api/auth/login', u, p),
    loginLocalAdmin: (u, p) => loginWithGateway('/api/auth/local-admin', u, p),
    sendPasswordReset,
    logout: () => endSession(null),
    isAdmin: user?.role === 'admin',
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
