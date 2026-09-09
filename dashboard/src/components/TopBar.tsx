import React, { useState } from 'react';
import {
    Box,
    Typography,
    IconButton,
    Tooltip,
    Menu,
    MenuItem,
    Badge,
    ListItemIcon,
    ListItemText,
} from '@mui/material';
import MenuIcon from '@mui/icons-material/Menu';
import NotificationsNoneIcon from '@mui/icons-material/NotificationsNone';
import NotificationsIcon from '@mui/icons-material/Notifications';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import { useLocation } from 'react-router-dom';
import { useUI } from '../contexts/UIContext';
import { useTheme } from '../contexts/ThemeContext';
import { useNotifications } from '../contexts/NotificationContext';
import BB8Toggle from './ui/star-wars-toggle-switch';

export const TopBar: React.FC = () => {
    const location = useLocation();
    const { sidebarOpen, toggleSidebar } = useUI();
    const { mode, toggleTheme } = useTheme();
    const { notifications, unreadCount } = useNotifications();
    const [notificationAnchor, setNotificationAnchor] = useState<null | HTMLElement>(null);

    const getTitle = () => {
        const path = location.pathname;
        if (path === '/dashboard' || path === '/') return 'Dashboard';
        if (path === '/database') return 'Database';
        return 'Dashboard';
    };


    return (
        <Box
            sx={{
                height: 80,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                px: 4,
                backgroundColor: (theme) => theme.palette.background.default,
                borderBottom: (theme) => `1px solid ${theme.palette.divider}`,
                position: 'sticky',
                top: 0,
                zIndex: 1100,
                transition: 'background-color 0.3s ease, border-color 0.3s ease',
            }}
        >
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                {!sidebarOpen && (
                    <Tooltip title="Show Sidebar">
                        <IconButton
                            onClick={toggleSidebar}
                            sx={{
                                color: (theme) => theme.palette.text.secondary,
                                '&:hover': {
                                    color: (theme) => theme.palette.text.primary,
                                    backgroundColor: (theme) => theme.palette.mode === 'dark'
                                        ? 'rgba(255,255,255,0.05)'
                                        : 'rgba(0,0,0,0.05)',
                                },
                                transition: 'background-color 0.3s ease, color 0.3s ease',
                            }}
                        >
                            <MenuIcon />
                        </IconButton>
                    </Tooltip>
                )}
                <Typography
                    variant="h5"
                    fontWeight={700}
                    sx={{ color: (theme) => theme.palette.text.primary }}
                >
                    {getTitle()}
                </Typography>
            </Box>

            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                    {/* BB8 Theme Toggle — unchecked = dark (night), checked = light (day) */}
                    {/* bb8-theme-toggle class exempts this node from the theme-switching CSS override */}
                    <Tooltip title={mode === 'dark' ? 'Switch to Light Mode' : 'Switch to Dark Mode'}>
                        <Box className="bb8-theme-toggle" sx={{ display: 'flex', alignItems: 'center' }}>
                            <BB8Toggle
                                checked={mode === 'light'}
                                onChange={toggleTheme}
                            />
                        </Box>
                    </Tooltip>

                    <Tooltip title={unreadCount > 0 ? `${unreadCount} spare${unreadCount === 1 ? '' : 's'} overdue for replacement` : 'Notifications'}>
                        <IconButton
                            onClick={(e) => setNotificationAnchor(e.currentTarget)}
                            sx={{
                                color: (theme) => theme.palette.text.secondary,
                                '&:hover': {
                                    color: (theme) => theme.palette.text.primary,
                                    backgroundColor: (theme) => theme.palette.mode === 'dark'
                                        ? 'rgba(255,255,255,0.05)'
                                        : 'rgba(0,0,0,0.05)',
                                },
                                transition: 'background-color 0.3s ease, color 0.3s ease',
                            }}
                        >
                            <Badge badgeContent={unreadCount} color="error" max={99}>
                                {unreadCount > 0 ? <NotificationsIcon /> : <NotificationsNoneIcon />}
                            </Badge>
                        </IconButton>
                    </Tooltip>
                    <Menu
                        anchorEl={notificationAnchor}
                        open={Boolean(notificationAnchor)}
                        onClose={(_, reason) => {
                            if (reason === 'backdropClick' || reason === 'escapeKeyDown') {
                                setNotificationAnchor(null);
                            }
                        }}
                        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
                        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
                        slotProps={{
                            paper: {
                                sx: {
                                    minWidth: 340,
                                    maxWidth: 420,
                                    maxHeight: '70vh',
                                    overflow: 'hidden',
                                    display: 'flex',
                                    flexDirection: 'column',
                                },
                            },
                        }}
                        MenuListProps={{
                            sx: { py: 0, maxHeight: '70vh', overflow: 'auto' },
                        }}
                    >
                        {notifications.length === 0 ? (
                            <MenuItem disabled sx={{ cursor: 'default' }}>
                                <ListItemText primary="No notifications" secondary="No spare is overdue for replacement." />
                            </MenuItem>
                        ) : (
                            notifications.map((n) => (
                                <MenuItem
                                    key={n.id}
                                    dense
                                    disableRipple
                                    sx={{ cursor: 'default', whiteSpace: 'normal' }}
                                >
                                    <ListItemIcon sx={{ minWidth: 36, alignSelf: 'flex-start', mt: 0.5 }}>
                                        {n.severity === 'error' ? (
                                            <ErrorOutlineIcon fontSize="small" color="error" />
                                        ) : n.severity === 'warning' ? (
                                            <WarningAmberIcon fontSize="small" color="warning" />
                                        ) : (
                                            <InfoOutlinedIcon fontSize="small" color="info" />
                                        )}
                                    </ListItemIcon>
                                    <ListItemText
                                        primary={n.title}
                                        secondary={n.message}
                                        primaryTypographyProps={{ fontWeight: 600, fontSize: '0.875rem' }}
                                        secondaryTypographyProps={{
                                            fontSize: '0.8rem',
                                            sx: { mt: 0.25 },
                                            style: { wordBreak: 'break-word' },
                                        }}
                                    />
                                </MenuItem>
                            ))
                        )}
                    </Menu>
            </Box>
        </Box>
    );
};
