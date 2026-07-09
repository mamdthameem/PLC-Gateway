import React, { useEffect } from 'react';
import { Navigate } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import { useAuth } from '../contexts/AuthContext';
import type { UserRole } from '../types';
import { isUserExpired } from '../utils/formatters';

interface ProtectedRouteProps {
  children: React.ReactNode;
  requiredRole?: UserRole;
}

export const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ 
  children, 
  requiredRole 
}) => {
  const { user, isLoading, logout } = useAuth();
  const isExpired = isUserExpired(user?.validUntil);

  useEffect(() => {
    if (user && (isExpired || user.isApproved === false)) {
      logout();
    }
  }, [user?.id, user?.isApproved, user?.validUntil, isExpired, logout]);

  if (isLoading) {
    return (
      <Box 
        display="flex" 
        justifyContent="center" 
        alignItems="center" 
        minHeight="100vh"
      >
        <CircularProgress />
      </Box>
    );
  }

  if (!user) {
    return <Navigate to="/login" replace />;
  }

  if (isExpired || user.isApproved === false) {
    return <Navigate to="/login" replace />;
  }

  if (requiredRole && user.role !== requiredRole) {
    return <Navigate to="/dashboard" replace />;
  }

  return <>{children}</>;
};

