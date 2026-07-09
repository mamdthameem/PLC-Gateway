import React, { useState } from 'react';
import { Avatar, Tooltip } from '@mui/material';
import { motion } from 'framer-motion';
import { useLocation } from 'react-router-dom';
import DashboardIcon from '@mui/icons-material/Dashboard';
import PeopleIcon from '@mui/icons-material/People';
import LogoutIcon from '@mui/icons-material/Logout';
import { useAuth } from '../contexts/AuthContext';
import { useUI } from '../contexts/UIContext';
import {
  SidebarRoot,
  DesktopSidebar,
  SidebarLink,
  useSidebar,
  type SidebarLinkItem,
} from './ui/sidebar';

// ─── Inner content — reads the animated open state from context ──────────────
// Deterministic colour from a string — same name always gets the same colour
const nameToColor = (name: string = ''): string => {
  const palette = [
    '#6366f1', '#8b5cf6', '#ec4899', '#f43f5e',
    '#f97316', '#eab308', '#22c55e', '#14b8a6',
    '#3b82f6', '#06b6d4', '#a855f7', '#d946ef',
  ];
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = name.charCodeAt(i) + ((hash << 5) - hash);
  }
  return palette[Math.abs(hash) % palette.length];
};

const SidebarInner: React.FC = () => {
  const { open } = useSidebar();
  const { user, logout, isAdmin } = useAuth();
  const location = useLocation();
  const [logoutHover, setLogoutHover] = useState(false);

  const isActive = (path: string) => location.pathname === path;

  const menuItems: SidebarLinkItem[] = [
    {
      label: 'Dashboard',
      to: '/dashboard',
      icon: <DashboardIcon sx={{ fontSize: 20 }} />,
      onClick: (e) => {
        if (location.pathname === '/dashboard') {
          e.preventDefault();
          window.scrollTo({ top: 0, behavior: 'smooth' });
        }
      },
    },
  ];

  if (isAdmin) {
    menuItems.push({
      label: 'Subscribed Users',
      to: '/users',
      icon: <PeopleIcon sx={{ fontSize: 20 }} />,
    });
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', justifyContent: 'space-between' }}>

      {/* ── Top: Logo + Nav ── */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>

        {/* Logo row */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '0 8px', marginBottom: 20, overflow: 'hidden' }}>
          <img
            src="/Untitled_design-removebg-preview.png"
            alt="SENSE SHOT"
            style={{ width: 32, height: 28, objectFit: 'contain', flexShrink: 0 }}
          />
          <motion.span
            animate={{ opacity: open ? 1 : 0, x: open ? 0 : -8 }}
            transition={{ duration: 0.2 }}
            style={{
              fontSize: '0.88rem',
              fontWeight: 800,
              letterSpacing: '0.08em',
              color: 'var(--sidebar-foreground)',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              pointerEvents: 'none',
            }}
          >
            SENSE SHOT
          </motion.span>
        </div>

        {/* Section label */}
        <motion.div
          animate={{ opacity: open ? 1 : 0 }}
          transition={{ duration: 0.15 }}
          style={{
            fontSize: '0.62rem',
            fontWeight: 700,
            letterSpacing: '0.12em',
            color: 'var(--muted-foreground)',
            paddingLeft: 10,
            marginBottom: 6,
            whiteSpace: 'nowrap',
            overflow: 'hidden',
          }}
        >
          MAIN MENU
        </motion.div>

        {/* Nav items */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          {menuItems.map((item) => (
            <SidebarLink
              key={item.to}
              link={item}
              isActive={isActive(item.to)}
            />
          ))}
        </div>
      </div>

      {/* ── Bottom: User profile ── */}
      <div style={{ borderTop: '1px solid var(--sidebar-border)', paddingTop: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '6px 8px', borderRadius: 12, overflow: 'hidden' }}>

          {/* Avatar */}
          <Avatar
            sx={{
              width: 32,
              height: 32,
              fontSize: '0.8rem',
              fontWeight: 700,
              flexShrink: 0,
              backgroundColor: nameToColor(user?.name),
              color: '#ffffff',
            }}
          >
            {user?.name?.charAt(0)?.toUpperCase()}
          </Avatar>

          {/* Name + email */}
          <motion.div
            animate={{ opacity: open ? 1 : 0, x: open ? 0 : -6 }}
            transition={{ duration: 0.2 }}
            style={{ display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1, overflow: 'hidden' }}
          >
            <span style={{ fontSize: '0.8rem', fontWeight: 700, color: 'var(--sidebar-foreground)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {user?.name}
            </span>
            <span style={{ fontSize: '0.67rem', color: 'var(--muted-foreground)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {user?.email}
            </span>
          </motion.div>

          {/* Logout */}
          <motion.div
            animate={{ opacity: open ? 1 : 0 }}
            transition={{ duration: 0.15 }}
            style={{ flexShrink: 0 }}
          >
            <Tooltip title="Logout" placement="right">
              <button
                onClick={logout}
                onMouseEnter={() => setLogoutHover(true)}
                onMouseLeave={() => setLogoutHover(false)}
                style={{
                  background: 'none',
                  border: 'none',
                  cursor: 'pointer',
                  padding: 4,
                  borderRadius: 8,
                  color: logoutHover ? 'var(--sidebar-foreground)' : 'var(--muted-foreground)',
                  display: 'flex',
                  alignItems: 'center',
                  transition: 'color 0.15s ease',
                }}
              >
                <LogoutIcon sx={{ fontSize: 18 }} />
              </button>
            </Tooltip>
          </motion.div>
        </div>
      </div>

    </div>
  );
};

// ─── Public Sidebar component ─────────────────────────────────────────────────
export const Sidebar: React.FC = () => {
  const { sidebarOpen } = useUI();
  const [open, setOpen] = useState(false);

  if (!sidebarOpen) return null;

  return (
    <SidebarRoot open={open} setOpen={setOpen} animate>
      <DesktopSidebar
        style={{
          position: 'fixed',
          left: 0,
          top: 0,
          height: '100vh',
          zIndex: 1200,
          backgroundColor: 'var(--sidebar)',
          borderRight: '1px solid var(--sidebar-border)',
          padding: '20px 10px',
          overflowX: 'hidden',
        }}
      >
        <SidebarInner />
      </DesktopSidebar>
    </SidebarRoot>
  );
};
