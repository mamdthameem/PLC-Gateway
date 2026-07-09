import { useState } from 'react';
import {
  Paper, Typography, Box, Chip, Dialog, DialogTitle,
  DialogContent, IconButton, Tooltip,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import { formatParameterValue, PARAM_META } from '../utils/unitConverters';

interface Props {
  parameterName: string;
  value: string;
  updatedAt?: string;
  graphTitle?: string;
  renderGraph?: () => React.ReactNode;
}

export default function ExpandableMetricCard({
  parameterName, value, updatedAt, graphTitle, renderGraph,
}: Props) {
  const [open, setOpen] = useState(false);

  const meta      = PARAM_META[parameterName];
  const label     = meta?.label ?? parameterName;
  const formatted = formatParameterValue(parameterName, value);
  const isStatus  = parameterName === 'machine_status';
  const isOn      = value === '1';
  const hasGraph  = Boolean(renderGraph);

  return (
    <>
      <Paper
        onClick={hasGraph ? () => setOpen(true) : undefined}
        sx={{
          p: 2.5,
          borderRadius: 2,
          display: 'flex',
          flexDirection: 'column',
          gap: 0.5,
          height: '100%',
          cursor: hasGraph ? 'pointer' : 'default',
          position: 'relative',
          transition: 'box-shadow 0.15s',
          '&:hover': hasGraph ? { boxShadow: 4 } : {},
        }}
      >
        {hasGraph && (
          <Tooltip title="View chart">
            <BarChartIcon
              fontSize="small"
              sx={{ position: 'absolute', top: 10, right: 10, color: 'text.disabled', fontSize: 16 }}
            />
          </Tooltip>
        )}

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

        {isStatus ? (
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

      {hasGraph && (
        <Dialog open={open} onClose={() => setOpen(false)} maxWidth="md" fullWidth>
          <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            {graphTitle ?? label}
            <IconButton onClick={() => setOpen(false)} size="small">
              <CloseIcon />
            </IconButton>
          </DialogTitle>
          <DialogContent>
            {open && renderGraph?.()}
          </DialogContent>
        </Dialog>
      )}
    </>
  );
}
