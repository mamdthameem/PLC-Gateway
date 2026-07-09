import React, { useState, useEffect } from 'react';
import {
    Box,
    Typography,
    Container,
    Paper,
    Chip,
    Button,
    Table,
    TableBody,
    TableCell,
    TableContainer,
    TableHead,
    TableRow,
    IconButton,
    Dialog,
    DialogTitle,
    DialogContent,
    DialogActions,
    TextField,
    Switch,
    FormControlLabel,
    FormGroup,
    Checkbox,
    Divider,
    MenuItem,
    InputAdornment,
    Autocomplete,
} from '@mui/material';
import EditIcon from '@mui/icons-material/Edit';
import DeleteIcon from '@mui/icons-material/Delete';
import AddIcon from '@mui/icons-material/Add';
import Visibility from '@mui/icons-material/Visibility';
import VisibilityOff from '@mui/icons-material/VisibilityOff';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import { Menu } from '@mui/material';
import type { User } from '../types';
import { dataService } from '../services/dataService';
import { useAuth } from '../contexts/AuthContext';
import { useUI } from '../contexts/UIContext';
import { useSearchParams } from 'react-router-dom';
import { formatDate } from '../utils/formatters';
import { useTheme } from '../contexts/ThemeContext';
import { AssistedPasswordConfirmation } from './ui/assisted-password-confirmation';

export const UserManagement: React.FC = () => {
    const { user: currentUser } = useAuth();
    const { searchTerm } = useUI();
    const { mode } = useTheme();
    const [searchParams, setSearchParams] = useSearchParams();
    const filter = searchParams.get('filter');

    const [users, setUsers] = useState<User[]>([]);
    const [userDialogOpen, setUserDialogOpen] = useState(false);
    const [editingUser, setEditingUser] = useState<User | null>(null);
    const [showPassword, setShowPassword] = useState(false);
    const [confirmPassword, setConfirmPassword] = useState('');
    const [newUser, setNewUser] = useState<Partial<User>>({
        name: '',
        industryName: '',
        state: '',
        email: '',
        username: '',
        password: '',
        role: 'user',
        isApproved: false,
        unapprovedReason: '',
        assignedMachineIds: [],
    });

    // Menu state
    const [anchorEl, setAnchorEl] = useState<null | HTMLElement>(null);
    const [menuTargetUser, setMenuTargetUser] = useState<User | null>(null);

    // Delete confirmation state
    const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);
    const [confirmUsername, setConfirmUsername] = useState('');
    const [usernameError, setUsernameError] = useState(false);

    const handleMenuOpen = (event: React.MouseEvent<HTMLElement>, user: User) => {
        setAnchorEl(event.currentTarget);
        setMenuTargetUser(user);
    };

    const handleMenuClose = () => {
        setAnchorEl(null);
    };

    const handleEditClick = () => {
        if (menuTargetUser) {
            handleOpenUserDialog(menuTargetUser);
        }
        handleMenuClose();
    };

    const handleDeleteClick = () => {
        setDeleteDialogOpen(true);
        setConfirmUsername('');
        setUsernameError(false);
        handleMenuClose();
    };

    const processDelete = () => {
        const targetIdentifier = menuTargetUser?.username || menuTargetUser?.email || menuTargetUser?.name;
        if (confirmUsername === targetIdentifier) {
            if (menuTargetUser) {
                dataService.deleteUser(menuTargetUser.id);
                loadUsers();
                setDeleteDialogOpen(false);
                setMenuTargetUser(null);
            }
        } else {
            setUsernameError(true);
        }
    };

    useEffect(() => {
        loadUsers();
    }, [filter, searchTerm]); // Reload when filter or search changes

    const loadUsers = () => {
        let allUsers = dataService.getUsers();

        // Filter out admins (they are owners, not subscribed users)
        allUsers = allUsers.filter(u => u.role !== 'admin');

        if (filter === 'approved') {
            allUsers = allUsers.filter(u => u.isApproved);
        }

        if (searchTerm) {
            const term = searchTerm.toLowerCase();
            allUsers = allUsers.filter(u =>
                u.name.toLowerCase().includes(term) ||
                u.email.toLowerCase().includes(term)
            );
        }

        setUsers(allUsers);
    };

    const clearFilter = () => {
        setSearchParams({});
    };

    const handleOpenUserDialog = (userToEdit?: User) => {
        if (userToEdit) {
            setEditingUser(userToEdit);
            setNewUser({ ...userToEdit });
        } else {
            setEditingUser(null);
            setNewUser({
                name: '',
                industryName: '',
                state: '',
                email: '',
                username: '',
                password: '',
                role: 'user',
                isApproved: false,
                unapprovedReason: '',
                assignedMachineIds: [],
            });
        }
        setShowPassword(false);
        setConfirmPassword('');
        setUserDialogOpen(true);
    };

    const handleSaveUser = () => {
        // For new users, require passwords to match
        if (!editingUser && newUser.password !== confirmPassword) return;
        if (editingUser) {
            dataService.updateUser({ ...editingUser, ...newUser } as User);
        } else {
            const id = `user-${Date.now()}`;
            dataService.addUser({ ...newUser, id, createdAt: new Date() } as User);
        }
        setUserDialogOpen(false);
        loadUsers();
    };


    return (
        <Container maxWidth="xl" sx={{ transition: 'all 0.3s ease' }}>
            <Box mb={4} display="flex" justifyContent="space-between" alignItems="flex-end">
                <Box>
                    <Typography variant="h4" fontWeight={800} mb={1} sx={{ color: (theme) => theme.palette.text.primary }}>
                        Subscribed Users {filter === 'approved' && <Chip label="Approved Only" size="small" sx={{ ml: 2, backgroundColor: 'rgba(76, 175, 80, 0.1)', color: '#81c784', fontWeight: 700 }} />}
                    </Typography>
                    <Typography variant="body1" sx={{ color: (theme) => theme.palette.text.secondary }}>
                        {filter === 'approved'
                            ? 'Showing only approved and active subscribers.'
                            : 'Manage user access, approvals, and subscription validity.'}
                    </Typography>
                </Box>
                <Box sx={{ display: 'flex', gap: 2 }}>
                    {filter && (
                        <Button
                            variant="outlined"
                            size="small"
                            onClick={clearFilter}
                            sx={{
                                transition: 'all 0.3s ease',
                            }}
                        >
                            Clear Filter
                        </Button>
                    )}
                    <Button
                        variant="contained"
                        startIcon={<AddIcon />}
                        onClick={() => handleOpenUserDialog()}
                        sx={{
                            borderRadius: 2,
                            fontWeight: 700,
                            px: 3,
                            transition: 'all 0.3s ease',
                        }}
                    >
                        Add User
                    </Button>
                </Box>
            </Box>

            <TableContainer component={Paper} sx={{ borderRadius: 4, transition: 'all 0.3s ease' }}>
                <Table>
                    <TableHead>
                        <TableRow sx={{ backgroundColor: (theme) => theme.palette.mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.02)' }}>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>NAME</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>EMAIL</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>ROLE</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>STATUS</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>VALIDITY</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }}>ACCESS</TableCell>
                            <TableCell sx={{ fontWeight: 700, color: (theme) => theme.palette.text.secondary, fontSize: '0.75rem' }} align="right">ACTIONS</TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {users.map((u) => (
                            <TableRow
                                key={u.id}
                                sx={{
                                    '&:hover': {
                                        backgroundColor: (theme) => theme.palette.mode === 'dark'
                                            ? 'rgba(255,255,255,0.02)'
                                            : 'rgba(0,0,0,0.02)',
                                    },
                                    transition: 'background-color 0.3s ease',
                                }}
                            >
                                <TableCell sx={{ fontWeight: 600, color: (theme) => theme.palette.text.primary }}>{u.name}</TableCell>
                                <TableCell sx={{ color: (theme) => theme.palette.text.secondary }}>{u.email}</TableCell>
                                <TableCell>
                                    <Chip
                                        label={u.role.toUpperCase()}
                                        size="small"
                                        sx={{
                                            borderRadius: 1,
                                            fontSize: '0.65rem',
                                            fontWeight: 700,
                                            backgroundColor: (theme) => u.role === 'admin'
                                                ? (theme.palette.mode === 'dark' ? 'rgba(255,255,255,0.1)' : 'rgba(0,0,0,0.1)')
                                                : 'transparent',
                                            border: (theme) => `1px solid ${theme.palette.divider}`,
                                            color: (theme) => theme.palette.text.secondary,
                                            transition: 'all 0.3s ease',
                                        }}
                                    />
                                </TableCell>
                                <TableCell>
                                    {u.isApproved ? (
                                        <Chip label="APPROVED" size="small" sx={{ borderRadius: 1, fontSize: '0.65rem', fontWeight: 700, backgroundColor: 'rgba(76, 175, 80, 0.1)', color: '#81c784', border: '1px solid rgba(76, 175, 80, 0.2)' }} />
                                    ) : (
                                        <Chip label="PENDING" size="small" sx={{ borderRadius: 1, fontSize: '0.65rem', fontWeight: 700, backgroundColor: 'rgba(244, 67, 54, 0.1)', color: '#e57373', border: '1px solid rgba(244, 67, 54, 0.2)' }} />
                                    )}
                                </TableCell>
                                <TableCell sx={{ color: (theme) => theme.palette.text.secondary, fontSize: '0.85rem' }}>
                                    {u.validUntil ? formatDate(u.validUntil) : 'PERMANENT'}
                                </TableCell>
                                <TableCell sx={{ color: (theme) => theme.palette.text.secondary, fontSize: '0.85rem' }}>
                                    {u.assignedMachineIds?.length || 0} Machines
                                </TableCell>
                                <TableCell align="right">
                                    <IconButton
                                        size="small"
                                        onClick={(e) => handleMenuOpen(e, u)}
                                        sx={{
                                            color: (theme) => theme.palette.text.secondary,
                                            '&:hover': {
                                                color: (theme) => theme.palette.text.primary,
                                                backgroundColor: (theme) => theme.palette.mode === 'dark'
                                                    ? 'rgba(255,255,255,0.05)'
                                                    : 'rgba(0,0,0,0.05)',
                                            },
                                            transition: 'all 0.3s ease',
                                        }}
                                        disabled={u.id === currentUser?.id}
                                    >
                                        <MoreVertIcon fontSize="small" />
                                    </IconButton>
                                </TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </TableContainer>

            {/* User Dialog */}
            <Dialog
                open={userDialogOpen}
                onClose={() => setUserDialogOpen(false)}
                maxWidth="sm"
                fullWidth
                PaperProps={{
                    sx: {
                        borderRadius: 4,
                        transition: 'all 0.3s ease',
                    }
                }}
            >
                <DialogTitle sx={{ fontWeight: 800, color: (theme) => theme.palette.text.primary }}>
                    {editingUser ? 'Edit User' : 'Create New User'}
                </DialogTitle>
                <DialogContent>
                    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3, mt: 2 }}>
                        <TextField
                            label="Name"
                            fullWidth
                            value={newUser.name}
                            onChange={(e) => setNewUser({ ...newUser, name: e.target.value })}
                        />
                        <TextField
                            label="Industry Name"
                            fullWidth
                            value={newUser.industryName || ''}
                            onChange={(e) => setNewUser({ ...newUser, industryName: e.target.value })}
                        />
                        <Autocomplete
                            options={[
                                "Tamil Nadu", "Karnataka", "Maharashtra", "Kerala", "Telangana",
                                "Andhra Pradesh", "Gujarat", "Delhi", "Uttar Pradesh", "West Bengal"
                            ]}
                            value={newUser.state || ''}
                            onChange={(_, newValue) => setNewUser({ ...newUser, state: newValue || '' })}
                            onInputChange={(_, newInputValue) => {
                                // Allow manual entry of state name if not in list
                                if (![
                                    "Tamil Nadu", "Karnataka", "Maharashtra", "Kerala", "Telangana",
                                    "Andhra Pradesh", "Gujarat", "Delhi", "Uttar Pradesh", "West Bengal"
                                ].includes(newInputValue)) {
                                    setNewUser({ ...newUser, state: newInputValue });
                                }
                            }}
                            autoHighlight
                            selectOnFocus
                            handleHomeEndKeys
                            renderInput={(params) => (
                                <TextField
                                    {...params}
                                    label="State"
                                    fullWidth
                                    placeholder="Search or select state..."
                                />
                            )}
                            freeSolo
                            fullWidth
                        />
                        <TextField
                            label="Email Address"
                            fullWidth
                            value={newUser.email}
                            onChange={(e) => setNewUser({ ...newUser, email: e.target.value })}
                        />
                        <TextField
                            label="Username"
                            fullWidth
                            value={newUser.username || ''}
                            onChange={(e) => setNewUser({ ...newUser, username: e.target.value })}
                        />
                        <TextField
                            label="Password"
                            type={showPassword ? 'text' : 'password'}
                            fullWidth
                            value={newUser.password}
                            onChange={(e) => setNewUser({ ...newUser, password: e.target.value })}
                            InputProps={{
                                endAdornment: (
                                    <InputAdornment position="end">
                                        <IconButton
                                            aria-label="toggle password visibility"
                                            onClick={() => setShowPassword((prev) => !prev)}
                                            edge="end"
                                            size="small"
                                        >
                                            {showPassword ? <VisibilityOff /> : <Visibility />}
                                        </IconButton>
                                    </InputAdornment>
                                ),
                            }}
                        />
                        {/* Confirm Password — animated component, only shown when adding a new user */}
                        {!editingUser && (
                            <AssistedPasswordConfirmation
                                password={newUser.password || ''}
                                onChange={setConfirmPassword}
                                isDark={mode === 'dark'}
                            />
                        )}
                        <FormControlLabel
                            control={
                                <Switch
                                    checked={newUser.isApproved}
                                    onChange={(e) => setNewUser({
                                        ...newUser,
                                        isApproved: e.target.checked,
                                        unapprovedReason: e.target.checked ? '' : (newUser.unapprovedReason || '')
                                    })}
                                />
                            }
                            label="Approved for Access"
                            sx={{ '& .MuiTypography-root': { fontWeight: 700, fontSize: '0.9rem' } }}
                        />
                        {!newUser.isApproved && (
                            <TextField
                                label="Unapproved Reason"
                                fullWidth
                                value={newUser.unapprovedReason || ''}
                                onChange={(e) => setNewUser({ ...newUser, unapprovedReason: e.target.value })}
                                placeholder="Reason for blocking access"
                            />
                        )}
                        <TextField
                            label="Valid Until"
                            type="date"
                            fullWidth
                            InputLabelProps={{ shrink: true }}
                            value={newUser.validUntil ? new Date(newUser.validUntil).toISOString().split('T')[0] : ''}
                            onChange={(e) => setNewUser({ ...newUser, validUntil: new Date(e.target.value) })}
                        />

                        <Divider sx={{ my: 1 }} />

                        <Box>
                            <Typography variant="subtitle2" fontWeight={700} mb={2}>Machine Access</Typography>
                            <FormGroup sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 2 }}>
                                {dataService.getMachineTemplates().map((machine) => {
                                    const isAssigned = newUser.assignedMachineIds?.some(
                                        id => id === machine.id || id.startsWith(machine.id + '-')
                                    );
                                    return (
                                    <FormControlLabel
                                        key={machine.id}
                                        control={
                                            <Checkbox
                                                checked={!!isAssigned}
                                                onChange={() => {
                                                    const currentIds = newUser.assignedMachineIds || [];
                                                    const newIds = isAssigned
                                                        ? currentIds.filter(id => id !== machine.id && !id.startsWith(machine.id + '-'))
                                                        : [...currentIds, machine.id];
                                                    setNewUser({ ...newUser, assignedMachineIds: newIds });
                                                }}
                                                sx={{
                                                    color: (theme) => theme.palette.divider,
                                                    '&.Mui-checked': {
                                                        color: (theme) => theme.palette.primary.main
                                                    },
                                                    transition: 'all 0.3s ease',
                                                }}
                                            />
                                        }
                                        label={
                                            <Box>
                                                <Typography variant="caption" fontWeight={700} display="block">{machine.name}</Typography>
                                                <Typography variant="caption" sx={{ color: (theme) => theme.palette.text.secondary }}>{machine.location}</Typography>
                                            </Box>
                                        }
                                    />
                                    );
                                })}
                            </FormGroup>
                        </Box>
                    </Box>
                </DialogContent>
                <DialogActions sx={{ p: 3 }}>
                    <Button
                        onClick={() => setUserDialogOpen(false)}
                        sx={{
                            color: (theme) => theme.palette.text.secondary,
                            transition: 'all 0.3s ease',
                        }}
                    >
                        Cancel
                    </Button>
                    <Button
                        variant="contained"
                        onClick={handleSaveUser}
                        sx={{ transition: 'all 0.3s ease' }}
                    >
                        Save User
                    </Button>
                </DialogActions>
            </Dialog>

            {/* Actions Menu */}
            <Menu
                anchorEl={anchorEl}
                open={Boolean(anchorEl)}
                onClose={handleMenuClose}
                PaperProps={{
                    sx: {
                        borderRadius: 2,
                        mt: 1,
                        boxShadow: '0 4px 20px rgba(0,0,0,0.1)',
                        minWidth: 150,
                    }
                }}
            >
                <MenuItem onClick={handleEditClick} sx={{ fontWeight: 600, py: 1.5 }}>
                    <EditIcon fontSize="small" sx={{ mr: 2, color: 'text.secondary' }} />
                    Edit User
                </MenuItem>
                <Divider />
                <MenuItem onClick={handleDeleteClick} sx={{ fontWeight: 600, py: 1.5, color: '#f44336' }}>
                    <DeleteIcon fontSize="small" sx={{ mr: 2 }} />
                    Delete User
                </MenuItem>
            </Menu>

            {/* Delete Confirmation Dialog */}
            <Dialog
                open={deleteDialogOpen}
                onClose={() => setDeleteDialogOpen(false)}
                PaperProps={{ sx: { borderRadius: 4, p: 1 } }}
            >
                <DialogTitle>
                    <Typography variant="h5" fontWeight={800} color="error">Confirm Deletion</Typography>
                </DialogTitle>
                <DialogContent>
                    <Typography variant="body1" mb={3}>
                        This action is <strong>irreversible</strong>. This will permanently delete the user <strong>{menuTargetUser?.name}</strong>.
                    </Typography>
                    <Typography variant="body2" color="text.secondary" mb={2}>
                        Please type the user's identifier <strong>{menuTargetUser?.username || menuTargetUser?.email || menuTargetUser?.name}</strong> to confirm.
                    </Typography>
                    <TextField
                        fullWidth
                        size="small"
                        autoFocus
                        placeholder="Enter username"
                        value={confirmUsername}
                        onChange={(e) => {
                            setConfirmUsername(e.target.value);
                            setUsernameError(false);
                        }}
                        onKeyDown={(e) => {
                            if (e.key === 'Enter') {
                                processDelete();
                            }
                        }}
                        error={usernameError}
                        helperText={usernameError ? "Identifier does not match." : ""}
                    />
                </DialogContent>
                <DialogActions sx={{ p: 3 }}>
                    <Button onClick={() => setDeleteDialogOpen(false)}>Cancel</Button>
                    <Button
                        variant="contained"
                        color="error"
                        onClick={processDelete}
                        sx={{ fontWeight: 700, px: 3 }}
                    >
                        I understand, delete this user
                    </Button>
                </DialogActions>
            </Dialog>

        </Container>
    );
};
