import React, { useState } from 'react';
import {
  Box,
  Typography,
  TextField,
  Button,
  InputAdornment,
  IconButton,
  Alert,
} from '@mui/material';
import { motion, AnimatePresence } from 'framer-motion';
import { useAuth } from '../contexts/AuthContext';
import { useLocation, useNavigate } from 'react-router-dom';
import PersonOutlineIcon from '@mui/icons-material/PersonOutline';
import LockOutlinedIcon from '@mui/icons-material/LockOutlined';
import Visibility from '@mui/icons-material/Visibility';
import VisibilityOff from '@mui/icons-material/VisibilityOff';
import ArrowForwardIcon from '@mui/icons-material/ArrowForward';
import { SparklesCore } from './ui/sparkles';

// ─── Shared dark input style ──────────────────────────────────────────────────
const inputSx = {
  '& .MuiOutlinedInput-root': {
    bgcolor: 'rgba(255,255,255,0.05)',
    borderRadius: '12px',
    '& fieldset': { borderColor: 'rgba(255,255,255,0.14)' },
    '&:hover fieldset': { borderColor: 'rgba(255,255,255,0.3)' },
    '&.Mui-focused fieldset': { borderColor: '#ffffff', borderWidth: '1.5px' },
  },
  '& .MuiInputLabel-root': {
    color: 'rgba(255,255,255,0.4)',
    fontSize: '0.875rem',
  },
  '& .MuiInputLabel-root.Mui-focused': { color: '#ffffff' },
  '& .MuiInputBase-input': {
    color: 'rgba(255,255,255,0.92)',
    fontSize: '0.95rem',
    // Chrome/Edge paint autofilled inputs with a forced light background via this box-shadow
    // trick (the only reliable override); without it, autofilled username/password fields
    // show a light box that clashes with the rest of the dark form.
    '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus, &:-webkit-autofill:active': {
      WebkitBoxShadow: '0 0 0 1000px #1c1c1c inset',
      WebkitTextFillColor: 'rgba(255,255,255,0.92)',
      caretColor: '#ffffff',
      borderRadius: 'inherit',
      transition: 'background-color 9999s ease-in-out 0s',
    },
  },
};

/**
 * Which form is showing. "email" is the sign-in (one account per person, added on the cloud's Users
 * page); "local" is the emergency admin, reached by a deliberately quiet link that appears only on the
 * plant network. The shared username/password login was removed (2026-10-26).
 */
type Mode = 'email' | 'local';

const MODE_TEXT: Record<Mode, { subtitle: string; idLabel: string; idType: string; idAutocomplete: string }> = {
  email:  { subtitle: 'Sign in with your email to access your machine dashboard', idLabel: 'Email',    idType: 'email', idAutocomplete: 'username' },
  local:  { subtitle: 'Local admin: for when the internet is down. Plant network only', idLabel: 'Username', idType: 'text', idAutocomplete: 'off' },
};

const quietLinkSx = {
  background: 'none', border: 'none', p: 0, cursor: 'pointer',
  fontSize: '0.75rem', color: 'rgba(255,255,255,0.45)', textDecoration: 'underline',
  '&:hover': { color: '#ffffff' },
};

export const Login: React.FC = () => {
  const [username, setUsername]       = useState('');
  const [password, setPassword]       = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError]             = useState<string | null>(null);
  const [info, setInfo]               = useState<string | null>(null);
  const [loading, setLoading]         = useState(false);
  const [chosenMode, setChosenMode]   = useState<Mode | null>(null);
  const {
    options, optionsError, sessionMessage, loginWithEmail, loginLocalAdmin, sendPasswordReset,
  } = useAuth();
  const navigate   = useNavigate();
  const location   = useLocation();
  // Where ProtectedRoute sent the user from. Only a path inside this app is accepted, never a full
  // URL, so a crafted link cannot bounce a fresh sign-in to another site.
  const from = (location.state as { from?: unknown } | null)?.from;
  const returnTo = typeof from === 'string' && from.startsWith('/') && !from.startsWith('//') ? from : '/dashboard';

  const mode: Mode = chosenMode ?? 'email';
  const text = MODE_TEXT[mode];
  const shownError = error ?? optionsError ?? sessionMessage;

  const switchMode = (next: Mode) => {
    setChosenMode(next);
    setError(null);
    setInfo(null);
    setUsername('');
    setPassword('');
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setInfo(null);
    setLoading(true);
    try {
      const signIn = mode === 'email' ? loginWithEmail : loginLocalAdmin;
      const result = await signIn(username, password);
      if (result.success) {
        navigate(returnTo, { replace: true });
      } else {
        setError(result.error || 'Invalid credentials. Please try again.');
      }
    } catch {
      setError('An unexpected error occurred. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  // Firebase emails a link to set a new password. Nobody — not even an administrator — can see or
  // choose someone else's password; this link is the only way to recover one.
  const handleForgot = async () => {
    setError(null);
    setInfo(null);
    const result = await sendPasswordReset(username);
    if (result.success) {
      setInfo(`If ${username.trim()} has an account, an email with a link to set a new password is on its way. Check your inbox.`);
    } else {
      setError(result.error ?? 'Could not send the email. Try again.');
    }
  };

  return (
    <Box
      sx={{
        display: 'flex',
        minHeight: '100vh',
        bgcolor: '#0a0a0a',
        // On mobile show form only, no split
        flexDirection: { xs: 'column', md: 'row' },
      }}
    >
      {/* ── LEFT 50% — Sparkles + branding ─────────────────────────────── */}
      <Box
        sx={{
          width: { xs: '100%', md: '50%' },
          minHeight: { xs: '220px', md: '100vh' },
          position: 'relative',
          overflow: 'hidden',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          bgcolor: '#0a0a0a',
          // Subtle right border separator
          borderRight: { md: '1px solid rgba(255,255,255,0.06)' },
        }}
      >
        {/* Full-panel sparkles layer */}
        <Box
          sx={{
            position: 'absolute',
            inset: 0,
            width: '100%',
            height: '100%',
            zIndex: 0,
          }}
        >
          <SparklesCore
            id="login-sparkles"
            background="transparent"
            minSize={0.4}
            maxSize={1.4}
            particleDensity={90}
            particleColor="#ffffff"
            speed={1.2}
            className="w-full h-full"
          />
        </Box>

        {/* Radial vignette so particles fade at edges */}
        <Box
          sx={{
            position: 'absolute',
            inset: 0,
            zIndex: 1,
            background:
              'radial-gradient(ellipse 80% 80% at 50% 50%, transparent 40%, #0a0a0a 100%)',
            pointerEvents: 'none',
          }}
        />

        {/* Foreground content */}
        <Box
          sx={{
            position: 'relative',
            zIndex: 2,
            textAlign: 'center',
            px: { xs: 3, md: 6 },
            userSelect: 'none',
          }}
        >
          {/* Logo */}
          <motion.div
            initial={{ opacity: 0, scale: 0.85 }}
            animate={{ opacity: 1, scale: 1 }}
            transition={{ duration: 0.7, ease: 'easeOut' }}
          >
            <Box
              component="img"
              src={`${import.meta.env.BASE_URL}Untitled_design-removebg-preview.png`}
              alt="Shot Sense"
              sx={{
                // 398x209 source — box holds that ratio so `contain` letterboxes nothing.
                width: { xs: 132, md: 176, lg: 208 },
                height: { xs: 69, md: 92, lg: 109 },
                objectFit: 'contain',
                mb: 3,
                filter: 'brightness(1.1)',
              }}
            />
          </motion.div>

          {/* Product name */}
          <motion.div
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.7, delay: 0.15, ease: 'easeOut' }}
          >
            <Typography
              sx={{
                fontSize: { xs: '2rem', md: '3rem', lg: '3.8rem' },
                fontWeight: 900,
                letterSpacing: '-0.03em',
                color: '#ffffff',
                lineHeight: 1.05,
                mb: 1.5,
              }}
            >
              SHOT SENSE
            </Typography>
          </motion.div>

          {/* Gradient underline bar */}
          <motion.div
            initial={{ scaleX: 0, opacity: 0 }}
            animate={{ scaleX: 1, opacity: 1 }}
            transition={{ duration: 0.6, delay: 0.4, ease: 'easeOut' }}
            style={{ transformOrigin: 'center' }}
          >
            <Box
              sx={{
                height: 3,
                borderRadius: 2,
                background: 'linear-gradient(90deg, transparent, #ffffff, transparent)',
                mx: 'auto',
                width: { xs: 160, md: 260 },
                mb: 2.5,
                opacity: 0.35,
              }}
            />
          </motion.div>

          {/* Tagline */}
          <motion.div
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.6, delay: 0.5, ease: 'easeOut' }}
          >
            <Typography
              sx={{
                fontSize: { xs: '0.75rem', md: '0.82rem' },
                fontWeight: 500,
                letterSpacing: '0.22em',
                color: 'rgba(255,255,255,0.38)',
                textTransform: 'uppercase',
              }}
            >
              Industrial IoT Gateway
            </Typography>
          </motion.div>
        </Box>

        {/* Bottom "SYSTEM ONLINE" pill */}
        <Box
          sx={{
            position: 'absolute',
            bottom: 28,
            zIndex: 2,
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 2,
            py: 0.6,
            borderRadius: '20px',
            bgcolor: 'rgba(255,255,255,0.05)',
            border: '1px solid rgba(255,255,255,0.09)',
          }}
        >
          <Box
            sx={{
              width: 7,
              height: 7,
              borderRadius: '50%',
              bgcolor: '#22c55e',
              boxShadow: '0 0 8px rgba(34,197,94,0.9)',
              animation: 'pulse-dot 2.2s ease-in-out infinite',
              '@keyframes pulse-dot': {
                '0%, 100%': { opacity: 1 },
                '50%': { opacity: 0.25 },
              },
            }}
          />
          <Typography
            sx={{
              fontSize: '0.65rem',
              fontWeight: 700,
              letterSpacing: '0.12em',
              color: 'rgba(255,255,255,0.3)',
            }}
          >
            SYSTEM ONLINE · SECURED
          </Typography>
        </Box>
      </Box>

      {/* ── RIGHT 50% — Login form ──────────────────────────────────────── */}
      <Box
        sx={{
          width: { xs: '100%', md: '50%' },
          minHeight: { xs: 'auto', md: '100vh' },
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          bgcolor: '#111111',
          px: { xs: 4, sm: 6, md: 8, lg: 10 },
          py: { xs: 6, md: 8 },
        }}
      >
        <motion.div
          style={{ width: '100%', maxWidth: 400 }}
          initial={{ opacity: 0, x: 32 }}
          animate={{ opacity: 1, x: 0 }}
          transition={{ duration: 0.65, delay: 0.2, ease: 'easeOut' }}
        >
          {/* Mobile logo */}
          <Box
            sx={{
              display: { xs: 'flex', md: 'none' },
              alignItems: 'center',
              gap: 1.5,
              mb: 5,
            }}
          >
            <Box
              component="img"
              src={`${import.meta.env.BASE_URL}Untitled_design-removebg-preview.png`}
              alt="Shot Sense"
              sx={{ width: 62, height: 32, objectFit: 'contain' }}
            />
            <Typography
              sx={{
                fontSize: '1rem',
                fontWeight: 800,
                letterSpacing: '0.14em',
                color: '#fff',
              }}
            >
              SHOT SENSE
            </Typography>
          </Box>

          {/* Heading */}
          <Box mb={5}>
            <Typography
              sx={{
                fontSize: '2rem',
                fontWeight: 800,
                color: '#ffffff',
                letterSpacing: '-0.03em',
                lineHeight: 1.2,
                mb: 0.75,
              }}
            >
              Welcome back
            </Typography>
            <Typography
              sx={{
                fontSize: '0.85rem',
                color: 'rgba(255,255,255,0.36)',
                fontWeight: 400,
              }}
            >
              {text.subtitle}
            </Typography>
          </Box>

          {/* Password-reset email sent */}
          {info && (
            <Alert
              severity="success"
              sx={{
                mb: 2.5,
                bgcolor: 'rgba(34,197,94,0.1)',
                border: '1px solid rgba(34,197,94,0.3)',
                color: '#86efac',
                borderRadius: '12px',
                fontSize: '0.82rem',
                '& .MuiAlert-icon': { color: '#86efac' },
              }}
            >
              {info}
            </Alert>
          )}

          {/* Error — a failed sign-in, or why the last session ended */}
          <AnimatePresence>
            {shownError && (
              <motion.div
                initial={{ opacity: 0, height: 0, y: -6 }}
                animate={{ opacity: 1, height: 'auto', y: 0 }}
                exit={{ opacity: 0, height: 0, y: -6 }}
                transition={{ duration: 0.22 }}
                style={{ marginBottom: 20 }}
              >
                <Alert
                  severity="error"
                  sx={{
                    bgcolor: 'rgba(239,68,68,0.1)',
                    border: '1px solid rgba(239,68,68,0.3)',
                    color: '#fca5a5',
                    borderRadius: '12px',
                    fontSize: '0.82rem',
                    '& .MuiAlert-icon': { color: '#fca5a5' },
                  }}
                >
                  {shownError}
                </Alert>
              </motion.div>
            )}
          </AnimatePresence>

          {/* Form */}
          <Box component="form" onSubmit={handleSubmit} sx={{ width: '100%' }}>
            <TextField
              key={mode}
              margin="normal"
              required
              fullWidth
              label={text.idLabel}
              type={text.idType}
              autoComplete={text.idAutocomplete}
              autoFocus
              value={username}
              onChange={e => setUsername(e.target.value)}
              sx={{ ...inputSx, mb: 1 }}
              InputProps={{
                startAdornment: (
                  <InputAdornment position="start">
                    <PersonOutlineIcon
                      sx={{ fontSize: 20, color: 'rgba(255,255,255,0.32)' }}
                    />
                  </InputAdornment>
                ),
              }}
            />

            <TextField
              margin="normal"
              required
              fullWidth
              label="Password"
              type={showPassword ? 'text' : 'password'}
              autoComplete={mode === 'local' ? 'off' : 'current-password'}
              value={password}
              onChange={e => setPassword(e.target.value)}
              sx={inputSx}
              InputProps={{
                startAdornment: (
                  <InputAdornment position="start">
                    <LockOutlinedIcon
                      sx={{ fontSize: 20, color: 'rgba(255,255,255,0.32)' }}
                    />
                  </InputAdornment>
                ),
                endAdornment: (
                  <InputAdornment position="end">
                    <IconButton
                      onClick={() => setShowPassword(v => !v)}
                      edge="end"
                      sx={{
                        color: 'rgba(255,255,255,0.32)',
                        '&:hover': { color: '#ffffff' },
                      }}
                    >
                      {showPassword
                        ? <VisibilityOff sx={{ fontSize: 20 }} />
                        : <Visibility   sx={{ fontSize: 20 }} />}
                    </IconButton>
                  </InputAdornment>
                ),
              }}
            />

            {/* Sign-in button */}
            <Button
              type="submit"
              fullWidth
              variant="contained"
              disabled={loading}
              endIcon={!loading && <ArrowForwardIcon sx={{ fontSize: 18 }} />}
              sx={{
                mt: 4,
                py: 1.6,
                borderRadius: '12px',
                fontSize: '0.88rem',
                fontWeight: 800,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
                background: loading
                  ? 'rgba(255,255,255,0.1)'
                  : 'linear-gradient(135deg, #e2e8f0 0%, #ffffff 100%)',
                color: loading ? 'rgba(255,255,255,0.3)' : '#0a0a0a',
                boxShadow: loading ? 'none' : '0 4px 20px rgba(255,255,255,0.12)',
                transition: 'all 0.25s ease',
                '&:hover:not(:disabled)': {
                  background: 'linear-gradient(135deg, #f8fafc 0%, #e2e8f0 100%)',
                  boxShadow: '0 6px 28px rgba(255,255,255,0.18)',
                  transform: 'translateY(-1px)',
                },
                '&:disabled': { cursor: 'not-allowed' },
              }}
            >
              {loading ? 'Authenticating…' : 'Sign In'}
            </Button>
          </Box>

          {/* "Forgot password?" is for email accounts only. "Local admin login" is deliberately
              quiet, and the gateway only offers it to a browser on the plant network. */}
          <Box sx={{ mt: 2.5, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1.25 }}>
            {mode === 'email' && options?.firebase && (
              <Box component="button" type="button" onClick={handleForgot} sx={quietLinkSx}>
                Forgot password?
              </Box>
            )}
            {mode !== 'email' && options?.emailLogin && (
              <Box component="button" type="button" onClick={() => switchMode('email')} sx={quietLinkSx}>
                Sign in with your email instead
              </Box>
            )}
            {mode !== 'local' && options?.localAdmin && (
              <Box
                component="button"
                type="button"
                onClick={() => switchMode('local')}
                sx={{ ...quietLinkSx, fontSize: '0.68rem', color: 'rgba(255,255,255,0.22)', textDecoration: 'none' }}
              >
                Local admin login
              </Box>
            )}
          </Box>

          {/* Footer note */}
          <Typography
            sx={{
              mt: 5,
              fontSize: '0.7rem',
              color: 'rgba(255,255,255,0.18)',
              textAlign: 'center',
              letterSpacing: '0.04em',
            }}
          >
            © {new Date().getFullYear()} Shot Sense Technologies
          </Typography>
        </motion.div>
      </Box>
    </Box>
  );
};
