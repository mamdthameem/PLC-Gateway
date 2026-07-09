import React, { createContext, useContext, useMemo } from 'react';
import { useAuth } from './AuthContext';
import { dataService } from '../services/dataService';
import { formatDate, daysUntil } from '../utils/formatters';

export interface AppNotification {
  id: string;
  type: 'expiry' | 'info';
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

export const NotificationProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { user, isAdmin } = useAuth();

  const notifications = useMemo((): AppNotification[] => {
    const list: AppNotification[] = [];

    if (isAdmin) {
      const expiringUsers = dataService.getUsersExpiringWithinDays(7);
      expiringUsers.forEach((u, i) => {
        const d = daysUntil(u.validUntil);
        const dayText = d === 0 ? 'today' : d === 1 ? 'tomorrow' : `in ${d} days`;
        list.push({
          id: `expiry-${u.id}-${i}`,
          type: 'expiry',
          title: 'User access expiring',
          message: `${u.name || u.email} expires ${dayText} (${formatDate(u.validUntil)})`,
          severity: (d ?? 99) <= 1 ? 'warning' : 'info',
          createdAt: new Date(),
        });
      });
    }

    if (user?.validUntil && user.role !== 'admin') {
      const d = daysUntil(user.validUntil);
      if (d !== null && d >= 0) {
        const dayText = d === 0 ? 'today' : d === 1 ? 'tomorrow' : `in ${d} days`;
        list.push({
          id: 'my-expiry',
          type: 'expiry',
          title: 'Your access expires soon',
          message: `Your access expires ${dayText} (${formatDate(user.validUntil)}). Contact your administrator to renew.`,
          severity: d <= 3 ? 'warning' : 'info',
          createdAt: new Date(),
        });
      }
    }

    return list;
  }, [user?.id, user?.validUntil, isAdmin]);

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
