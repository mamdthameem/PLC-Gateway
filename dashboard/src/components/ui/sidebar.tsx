import React, { createContext, useContext, type ReactNode, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';
import { motion } from 'framer-motion';

export interface SidebarLinkItem {
  label: string;
  to: string;
  icon: ReactNode;
  onClick?: (e: React.MouseEvent) => void;
}

interface SidebarContextType {
  open: boolean;
}

const SidebarContext = createContext<SidebarContextType>({ open: false });

export const useSidebar = () => useContext(SidebarContext);

interface SidebarRootProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  animate?: boolean;
  children: ReactNode;
}

export const SidebarRoot: React.FC<SidebarRootProps> = ({ open, setOpen, children }) => (
  <SidebarContext.Provider value={{ open }}>
    <div onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)}>
      {children}
    </div>
  </SidebarContext.Provider>
);

interface DesktopSidebarProps {
  style?: CSSProperties;
  children: ReactNode;
}

export const DesktopSidebar: React.FC<DesktopSidebarProps> = ({ style, children }) => {
  const { open } = useSidebar();
  return (
    <motion.div
      animate={{ width: open ? 220 : 64 }}
      transition={{ duration: 0.25, ease: 'easeInOut' }}
      style={{ overflow: 'hidden', ...style }}
    >
      {children}
    </motion.div>
  );
};

interface SidebarLinkProps {
  link: SidebarLinkItem;
  isActive: boolean;
}

export const SidebarLink: React.FC<SidebarLinkProps> = ({ link, isActive }) => {
  const { open } = useSidebar();
  return (
    <Link
      to={link.to}
      onClick={link.onClick}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: '8px 10px',
        borderRadius: 10,
        textDecoration: 'none',
        backgroundColor: isActive ? 'rgba(255,255,255,0.08)' : 'transparent',
        color: isActive ? 'var(--sidebar-foreground)' : 'var(--muted-foreground)',
        transition: 'background-color 0.15s ease, color 0.15s ease',
        overflow: 'hidden',
        whiteSpace: 'nowrap',
      }}
      onMouseEnter={e => {
        if (!isActive) {
          (e.currentTarget as HTMLElement).style.backgroundColor = 'rgba(255,255,255,0.06)';
          (e.currentTarget as HTMLElement).style.color = 'var(--sidebar-foreground)';
        }
      }}
      onMouseLeave={e => {
        if (!isActive) {
          (e.currentTarget as HTMLElement).style.backgroundColor = 'transparent';
          (e.currentTarget as HTMLElement).style.color = 'var(--muted-foreground)';
        }
      }}
    >
      <span style={{ flexShrink: 0, display: 'flex', alignItems: 'center' }}>
        {link.icon}
      </span>
      <motion.span
        animate={{ opacity: open ? 1 : 0, x: open ? 0 : -6 }}
        transition={{ duration: 0.2 }}
        style={{ fontSize: '0.85rem', fontWeight: 600 }}
      >
        {link.label}
      </motion.span>
    </Link>
  );
};
