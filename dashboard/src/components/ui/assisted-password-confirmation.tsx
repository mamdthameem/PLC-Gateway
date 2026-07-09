import { motion } from 'framer-motion';
import { useEffect, useRef, useState } from 'react';

interface AssistedPasswordConfirmationProps {
  password: string;
  onChange: (value: string) => void;
  isDark?: boolean;
}

export function AssistedPasswordConfirmation({
  password,
  onChange,
  isDark = false,
}: AssistedPasswordConfirmationProps) {
  const [confirmPassword, setConfirmPassword] = useState('');
  const [shake, setShake] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  // Reset when password changes
  useEffect(() => {
    setConfirmPassword('');
    onChange('');
  }, [password]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (shake) {
      const t = setTimeout(() => setShake(false), 500);
      return () => clearTimeout(t);
    }
  }, [shake]);

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (
      confirmPassword.length >= password.length &&
      e.target.value.length > confirmPassword.length
    ) {
      setShake(true);
      return;
    }
    const next = e.target.value;
    setConfirmPassword(next);
    onChange(next);
  };

  const passwordsMatch = password.length > 0 && password === confirmPassword;

  // True only when at least one typed character mismatches the password at that position
  const hasMismatch = confirmPassword.split('').some((char, i) => char !== password[i]);

  // ── theme tokens ──────────────────────────────────────────────
  const cardBg     = isDark ? '#1a1a1a' : '#ffffff';
  const borderBase = isDark ? '#3f3f46' : '#d1d5db';
  const dotColor   = isDark ? '#e4e4e7' : '#18181b';
  const labelColor = isDark ? '#a1a1aa' : '#6b7280';
  const inputText  = isDark ? '#f4f4f5' : '#111111';
  const placeholderColor = isDark ? '#52525b' : '#9ca3af';

  // per-character background
  const charBg = (i: number): string => {
    if (!confirmPassword[i]) return 'transparent';
    return confirmPassword[i] === password[i]
      ? 'rgba(34,197,94,0.22)'   // green
      : 'rgba(239,68,68,0.22)';  // red
  };

  const DOT_W = 18; // px per character slot

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, width: '100%' }}>
      {/* Label */}
      <span style={{ fontSize: 11, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase', color: labelColor }}>
        Confirm Password
      </span>

      {/* ── Preview row ── */}
      <motion.div
        animate={{
          x: shake ? [-8, 8, -8, 8, 0] : 0,
          scale: passwordsMatch ? [1, 1.015, 1] : 1,
        }}
        transition={{ duration: shake ? 0.45 : 0.25 }}
        style={{
          position: 'relative',
          height: 52,
          width: '100%',
          borderRadius: 12,
          border: `2px solid ${passwordsMatch ? '#10b981' : borderBase}`,
          background: cardBg,
          overflow: 'hidden',
          transition: 'border-color 0.25s ease',
          display: 'flex',
          alignItems: 'center',
          paddingLeft: 12,
          paddingRight: 12,
        }}
      >
        {/* Colour-feedback layer (behind dots) */}
        <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', paddingLeft: 12 }}>
          {password.split('').map((_, i) => (
            <motion.div
              key={i}
              style={{
                width: DOT_W,
                height: '100%',
                background: charBg(i),
                flexShrink: 0,
                transformOrigin: 'left center',
              }}
              animate={{ scaleX: confirmPassword[i] ? 1 : 0 }}
              initial={{ scaleX: 0 }}
              transition={{ duration: 0.18 }}
            />
          ))}
        </div>

        {/* Dot row (above the colour layer) */}
        <div style={{ position: 'relative', zIndex: 1, display: 'flex', alignItems: 'center', gap: 0 }}>
          {password.split('').map((_, i) => (
            <div
              key={i}
              style={{ width: DOT_W, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}
            >
              <span style={{ width: 6, height: 6, borderRadius: '50%', background: dotColor, display: 'inline-block' }} />
            </div>
          ))}
        </div>
      </motion.div>

      {/* ── Actual input ── */}
      <motion.div
        animate={{ scale: passwordsMatch ? [1, 1.015, 1] : 1 }}
        transition={{ duration: 0.25 }}
        style={{
          height: 52,
          borderRadius: 12,
          border: `2px solid ${passwordsMatch ? '#10b981' : borderBase}`,
          background: cardBg,
          overflow: 'hidden',
          transition: 'border-color 0.25s ease',
        }}
      >
        <input
          ref={inputRef}
          type="password"
          placeholder="Confirm password"
          value={confirmPassword}
          onChange={handleChange}
          style={{
            width: '100%',
            height: '100%',
            background: 'transparent',
            border: 'none',
            outline: 'none',
            padding: '0 14px',
            fontSize: 14,
            letterSpacing: '0.35em',
            color: inputText,
            fontFamily: 'inherit',
          }}
          onFocus={e => (e.currentTarget.style.letterSpacing = '0.35em')}
        />
        <style>{`
          input::placeholder { color: ${placeholderColor}; letter-spacing: normal; }
        `}</style>
      </motion.div>

      {/* Helper — error only on wrong letter, success only when fully matched */}
      {(hasMismatch || passwordsMatch) && (
        <motion.span
          initial={{ opacity: 0, y: -4 }}
          animate={{ opacity: 1, y: 0 }}
          style={{
            fontSize: 11,
            fontWeight: 700,
            color: passwordsMatch ? '#10b981' : '#ef4444',
          }}
        >
          {passwordsMatch ? '✓ Passwords match' : 'Passwords do not match'}
        </motion.span>
      )}
    </div>
  );
}
