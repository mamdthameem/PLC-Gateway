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
import { useNavigate } from 'react-router-dom';
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
  '& .MuiInputBase-input': { color: 'rgba(255,255,255,0.92)', fontSize: '0.95rem' },
};

export const Login: React.FC = () => {
  const [username, setUsername]       = useState('');
  const [password, setPassword]       = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError]             = useState<string | null>(null);
  const [loading, setLoading]         = useState(false);
  const { login }  = useAuth();
  const navigate   = useNavigate();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const result = await login(username, password);
      if (result.success) {
        navigate('/dashboard');
      } else {
        setError(result.error || 'Invalid credentials. Please try again.');
      }
    } catch {
      setError('An unexpected error occurred. Please try again.');
    } finally {
      setLoading(false);
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
              src="/Untitled_design-removebg-preview.png"
              alt="Sense Shot"
              sx={{
                width: { xs: 52, md: 68 },
                height: { xs: 48, md: 64 },
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
              SENSE SHOT
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
              src="/Untitled_design-removebg-preview.png"
              alt="Sense Shot"
              sx={{ width: 34, height: 32, objectFit: 'contain' }}
            />
            <Typography
              sx={{
                fontSize: '1rem',
                fontWeight: 800,
                letterSpacing: '0.14em',
                color: '#fff',
              }}
            >
              SENSE SHOT
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
              Sign in to access your machine dashboard
            </Typography>
          </Box>

          {/* Error */}
          <AnimatePresence>
            {error && (
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
                  {error}
                </Alert>
              </motion.div>
            )}
          </AnimatePresence>

          {/* Form */}
          <Box component="form" onSubmit={handleSubmit} sx={{ width: '100%' }}>
            <TextField
              margin="normal"
              required
              fullWidth
              label="Username"
              autoComplete="username"
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
              autoComplete="current-password"
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
            © {new Date().getFullYear()} Sense Shot Technologies
          </Typography>
        </motion.div>
      </Box>
    </Box>
  );
};
