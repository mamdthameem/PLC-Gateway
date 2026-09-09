import React, { createContext, useContext, useEffect, useState } from 'react';
import { fetchSpareAlerts } from '../services/spareStatusService';
import { formatRunHours } from '../utils/unitConverters';
import { useAuth } from './AuthContext';
import type { SpareStatus } from '../types';

export interface AppNotification {
  id: string;
  type: 'spare' | 'info';
  title: string;
  message: string;
  severity: 'warning' | 'info' | 'error';
  createdAt: Date;
}

interface NotificationContextType {
  notifications: AppNotification[];
  unreadCount: number;
}

const NotificationContext = createContext<NotificationContextType | undefined>(undefined);

/**
 * The bell mirrors the red cells in the Spare Part Life table.
 *
 * `/api/sparestatus/alerts` is the same condition that paints a cell red and stamps it with the
 * "!" chip — trigger_active AND a real threshold — so the two can never disagree about what counts
 * as an alert. Spare index 9 carries threshold 0 ("skip"), and the endpoint excludes it for that
 * reason; a spare the plant does not track a limit for is not overdue.
 *
 * Polled slower than the table (30 s vs 10 s) because run-hour thresholds are crossed on the scale
 * of shifts, not seconds, and this request runs on every page rather than only where the table is.
 */
const POLL_MS = 30_000;

function toNotification(s: SpareStatus): AppNotification {
  return {
    id: `spare-${s.impellerNum}-${s.spareIndex}`,
    type: 'spare',
    title: `Impeller ${s.impellerNum}: ${s.spareName}`,
    message:
      `${formatRunHours(s.currentRunHours)} run against a ${formatRunHours(s.thresholdHours)} limit. ` +
      'Replacement due.',
    severity: 'error',
    createdAt: new Date(s.lastUpdatedAt),
  };
}

export const NotificationProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { user } = useAuth();
  const [notifications, setNotifications] = useState<AppNotification[]>([]);

  useEffect(() => {
    // Nothing to poll before login: the endpoint is JWT-protected and would only 401.
    if (!user) {
      setNotifications([]);
      return;
    }

    let active = true;

    async function load() {
      try {
        const alerts = await fetchSpareAlerts();
        if (!active) return;
        // Most overdue first, so the worst offender is the one visible without scrolling.
        const ordered = [...alerts].sort(
          (a, b) =>
            b.currentRunHours / (b.thresholdHours || 1) -
            a.currentRunHours / (a.thresholdHours || 1),
        );
        setNotifications(ordered.map(toNotification));
      } catch {
        // Non-fatal: the table carries the same information, so a failed poll leaves the bell
        // showing whatever it last knew rather than an error the user cannot act on.
      }
    }

    load();
    const id = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, [user]);

  const value: NotificationContextType = {
    notifications,
    unreadCount: notifications.length,
  };

  return (
    <NotificationContext.Provider value={value}>
      {children}
    </NotificationContext.Provider>
  );
};

export const useNotifications = (): NotificationContextType => {
  const context = useContext(NotificationContext);
  if (context === undefined) {
    throw new Error('useNotifications must be used within a NotificationProvider');
  }
  return context;
};
