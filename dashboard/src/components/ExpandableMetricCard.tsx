import { useState } from 'react';
import {
  Paper, Typography, Box, Chip, Dialog, DialogTitle,
  DialogContent, IconButton, Tooltip, CircularProgress,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import { formatParameterValue, PARAM_META } from '../utils/unitConverters';

interface Props {
  parameterName: string;
  value: string;
  updatedAt?: string;
  graphTitle?: string;
  /**
   * What the chart measures, in prose. Sits behind an info icon next to the dialog title rather
   * than in a caption under it: it is reference material a reader wants once, not every time.
   */
  graphInfo?: string;
  renderGraph?: () => React.ReactNode;
  /**
   * Section 2 tiles pass this so a parameter whose formula differs between the sections picks up
   * PARAM_META.section2Label instead of the Section 1 name. Both sections are on screen at once,
   * so Production appears twice: "Production (Tonnage)" above, "Production (Item Weight)" below.
   */
  section?: 1 | 2;
}

export default function ExpandableMetricCard({
  parameterName, value, updatedAt, graphTitle, graphInfo, renderGraph, section = 1,
}: Props) {
  const [open, setOpen] = useState(false);

  // Recharts' ResponsiveContainer sizes itself from its parent. Mounted while the dialog is still
  // animating open, it can measure zero width and render nothing. Waiting for the transition to
  // finish guarantees it measures a settled container.
  const [chartReady, setChartReady] = useState(false);

  const closeDialog = () => { setOpen(false); setChartReady(false); };

  const meta      = PARAM_META[parameterName];
  const label     = (section === 2 ? meta?.section2Label : undefined) ?? meta?.label ?? parameterName;
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
        <Dialog
          open={open}
          onClose={closeDialog}
          maxWidth="lg"
          fullWidth
          slotProps={{ transition: { onEntered: () => setChartReady(true) } }}
        >
          <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75 }}>
              {/* Defaults to the tile's own name: the dialog opened FROM that tile, so a separate
                  title can only repeat it or contradict it. */}
              {graphTitle ?? label}
              {graphInfo && (
                <Tooltip title={graphInfo}>
                  <InfoOutlinedIcon sx={{ fontSize: 17, color: 'text.disabled', cursor: 'help' }} />
                </Tooltip>
              )}
            </Box>
            <IconButton onClick={closeDialog} size="small">
              <CloseIcon />
            </IconButton>
          </DialogTitle>
          {/* Charts need room: a dense series in a 600 px dialog was the reason axis labels had to
              be thinned to the point of disappearing. */}
          <DialogContent sx={{ pb: 3 }}>
            {chartReady ? renderGraph?.() : (
              <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: 420 }}>
                <CircularProgress />
              </Box>
            )}
          </DialogContent>
        </Dialog>
      )}
    </>
  );
}
