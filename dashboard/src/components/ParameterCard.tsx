import React from 'react';
import { Paper, Typography, Box, Chip } from '@mui/material';
import { formatParameterValue, PARAM_META } from '../utils/unitConverters';

interface Props {
  parameterName: string;
  value: string;
  updatedAt?: string;
}

export const ParameterCard: React.FC<Props> = ({ parameterName, value, updatedAt }) => {
  const meta = PARAM_META[parameterName];
  const label = meta?.label ?? parameterName;
  const formatted = formatParameterValue(parameterName, value);

  const isStatusParam = parameterName === 'machine_status';
  const isOn = value === '1';

  return (
    <Paper
      sx={{
        p: 2.5,
        borderRadius: 2,
        display: 'flex',
        flexDirection: 'column',
        gap: 0.5,
        height: '100%',
      }}
    >
      <Typography
        variant="caption"
        sx={{
          color: 'text.secondary',
          fontWeight: 600,
          letterSpacing: '0.07em',
          fontSize: '0.68rem',
          textTransform: 'uppercase',
        }}
      >
        {label}
      </Typography>

      {isStatusParam ? (
        <Box mt={0.5}>
          <Chip
            label={isOn ? 'ON' : 'OFF'}
            size="small"
            sx={{
              fontWeight: 700,
              fontSize: '0.85rem',
              backgroundColor: isOn ? 'rgba(34,197,94,0.15)' : 'rgba(239,68,68,0.15)',
              color: isOn ? '#22c55e' : '#ef4444',
              border: `1px solid ${isOn ? '#22c55e' : '#ef4444'}`,
            }}
          />
        </Box>
      ) : (
        <Typography
          variant="h6"
          fontWeight={700}
          sx={{ fontSize: '1.3rem', color: 'text.primary', lineHeight: 1.2, mt: 0.5 }}
        >
          {formatted}
        </Typography>
      )}

      {updatedAt && (
        <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', mt: 'auto' }}>
          {new Date(updatedAt).toLocaleTimeString()}
        </Typography>
      )}
    </Paper>
  );
};
