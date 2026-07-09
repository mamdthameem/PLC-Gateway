import React from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ThemeProvider as MuiThemeProvider, CssBaseline, Box } from '@mui/material';
import { AuthProvider } from './contexts/AuthContext';
import { UIProvider, useUI } from './contexts/UIContext';
import { ThemeProvider, useTheme } from './contexts/ThemeContext';
import { NotificationProvider } from './contexts/NotificationContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { Login } from './components/Login';
import { MachineDashboard } from './components/MachineDashboard';
import { UserManagement } from './components/UserManagement';
import { Sidebar, TopBar } from './components';

const Layout: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { theme } = useTheme();
  const { sidebarOpen } = useUI();
  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', backgroundColor: theme.palette.background.default }}>
      <Sidebar />
      <Box
        sx={{
          flexGrow: 1,
          ml: sidebarOpen ? '64px' : 0,
          display: 'flex',
          flexDirection: 'column',
          transition: 'margin-left 0.3s ease',
        }}
      >
        <TopBar />
        <Box component="main" sx={{ p: 2, flexGrow: 1, backgroundColor: theme.palette.background.default }}>
          {children}
        </Box>
      </Box>
    </Box>
  );
};

const ThemeWrapper: React.FC = () => {
  const { theme } = useTheme();
  return (
    <MuiThemeProvider theme={theme}>
      <CssBaseline />
      <AuthProvider>
        <NotificationProvider>
          <UIProvider>
            <BrowserRouter>
              <Routes>
                <Route path="/login" element={<Login />} />
                <Route
                  path="/dashboard"
                  element={
                    <ProtectedRoute>
                      <Layout>
                        <MachineDashboard />
                      </Layout>
                    </ProtectedRoute>
                  }
                />
                <Route
                  path="/users"
                  element={
                    <ProtectedRoute requiredRole="admin">
                      <Layout>
                        <UserManagement />
                      </Layout>
                    </ProtectedRoute>
                  }
                />
                <Route path="/" element={<Navigate to="/dashboard" replace />} />
                <Route path="*" element={<Navigate to="/dashboard" replace />} />
              </Routes>
            </BrowserRouter>
          </UIProvider>
        </NotificationProvider>
      </AuthProvider>
    </MuiThemeProvider>
  );
};

function App() {
  return (
    <ThemeProvider>
      <ThemeWrapper />
    </ThemeProvider>
  );
}

export default App;
