import React, { createContext, useContext } from 'react';

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

// This single-site app has no user-subscription/expiry concept, so there are currently no
// notifications. The provider is kept as the seam for future PLC/maintenance alerts.
export const NotificationProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const value: NotificationContextType = { notifications: [], unreadCount: 0 };
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
