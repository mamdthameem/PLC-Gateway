import React, { useEffect, useState } from 'react';
import {
    Box,
    Typography,
    TextField,
    InputAdornment,
    IconButton,
    Tooltip,
    Menu,
    MenuItem,
    Badge,
    ListItemIcon,
    ListItemText,
} from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import ClearIcon from '@mui/icons-material/Clear';
import MenuIcon from '@mui/icons-material/Menu';
import NotificationsNoneIcon from '@mui/icons-material/NotificationsNone';
import NotificationsIcon from '@mui/icons-material/Notifications';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import KeyboardCommandKeyIcon from '@mui/icons-material/KeyboardCommandKey';
import { useLocation } from 'react-router-dom';
import { useUI } from '../contexts/UIContext';
import { useTheme } from '../contexts/ThemeContext';
import { useNotifications } from '../contexts/NotificationContext';
import BB8Toggle from './ui/star-wars-toggle-switch';

export const TopBar: React.FC = () => {
    const location = useLocation();
    const { searchTerm, setSearchTerm, sidebarOpen, toggleSidebar } = useUI();
    const { mode, toggleTheme } = useTheme();
    const { notifications, unreadCount } = useNotifications();
    const [notificationAnchor, setNotificationAnchor] = useState<null | HTMLElement>(null);

    // Reset search term when navigating between pages
    useEffect(() => {
        setSearchTerm('');
    }, [location.pathname, setSearchTerm]);

    const getTitle = () => {
        const path = location.pathname;
        if (path === '/dashboard' || path === '/') return 'Dashboard';
        if (path === '/database') return 'Database';
        if (path === '/users') return 'Subscribed Users';
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

            <Box sx={{ display: 'flex', alignItems: 'center', gap: 3 }}>
                {/* Search */}
                <TextField
                    size="small"
                    placeholder="Search anything..."
                    variant="outlined"
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                    InputProps={{
                        startAdornment: (
                            <InputAdornment position="start">
                                <SearchIcon
                                    sx={{
                                        color: (theme) => theme.palette.mode === 'dark'
                                            ? 'rgba(255,255,255,0.3)'
                                            : 'rgba(0,0,0,0.4)',
                                        fontSize: 20
                                    }}
                                />
                            </InputAdornment>
                        ),
                        endAdornment: (
                            <InputAdornment position="end">
                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                                    {searchTerm && (
                                        <IconButton
                                            size="small"
                                            onClick={() => setSearchTerm('')}
                                            sx={{
                                                color: (theme) => theme.palette.text.secondary,
                                                '&:hover': {
                                                    color: (theme) => theme.palette.text.primary,
                                                    backgroundColor: (theme) => theme.palette.mode === 'dark'
                                                        ? 'rgba(255,255,255,0.08)'
                                                        : 'rgba(0,0,0,0.08)',
                                                },
                                                transition: 'all 0.2s ease',
                                            }}
                                        >
                                            <ClearIcon fontSize="small" />
                                        </IconButton>
                                    )}
                                    <Box
                                        sx={{
                                            display: 'flex',
                                            alignItems: 'center',
                                            gap: 0.5,
                                            backgroundColor: (theme) => theme.palette.mode === 'dark'
                                                ? 'rgba(255,255,255,0.05)'
                                                : 'rgba(0,0,0,0.05)',
                                            px: 0.8,
                                            py: 0.2,
                                            borderRadius: 1,
                                            border: (theme) => `1px solid ${theme.palette.divider}`,
                                            transition: 'background-color 0.3s ease, border-color 0.3s ease',
                                        }}
                                    >
                                        <KeyboardCommandKeyIcon
                                            sx={{
                                                fontSize: 12,
                                                color: (theme) => theme.palette.text.secondary
                                            }}
                                        />
                                        <Typography
                                            sx={{
                                                fontSize: 10,
                                                color: (theme) => theme.palette.text.secondary,
                                                fontWeight: 700
                                            }}
                                        >
                                            K
                                        </Typography>
                                    </Box>
                                </Box>
                            </InputAdornment>
                        ),
                    }}
                    sx={{
                        width: 320,
                        '& .MuiOutlinedInput-root': {
                            backgroundColor: (theme) => theme.palette.mode === 'dark'
                                ? 'rgba(255,255,255,0.03)'
                                : 'rgba(0,0,0,0.02)',
                            borderRadius: 2,
                            transition: 'background-color 0.3s ease, border-color 0.3s ease',
                            '& fieldset': {
                                borderColor: (theme) => theme.palette.mode === 'dark'
                                    ? 'transparent'
                                    : 'rgba(0,0,0,0.12)',
                                transition: 'border-color 0.3s ease',
                            },
                            '&:hover fieldset': {
                                borderColor: (theme) => theme.palette.divider
                            },
                            '&.Mui-focused fieldset': {
                                borderColor: (theme) => theme.palette.primary.main
                            },
                        },
                        '& .MuiInputBase-input': {
                            color: (theme) => theme.palette.text.primary,
                            transition: 'color 0.3s ease',
                            '&::placeholder': {
                                color: (theme) => theme.palette.text.secondary,
                                opacity: 1,
                            },
                        },
                    }}
                />

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

                    <Tooltip title={unreadCount > 0 ? `${unreadCount} notification(s)` : 'Notifications'}>
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
                            <Badge badgeContent={unreadCount} color="warning" max={99}>
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
                                <ListItemText primary="No notifications" secondary="You're all set." />
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
                                        {n.severity === 'warning' ? (
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
        </Box>
    );
};
