import React, { createContext, useContext, useState, useEffect, useMemo } from 'react';
import { createTheme } from '@mui/material';
import type { Theme } from '@mui/material/styles';

type ThemeMode = 'light' | 'dark';

interface ThemeContextType {
  mode: ThemeMode;
  toggleTheme: () => void;
  setMode: (mode: ThemeMode) => void;
  theme: Theme;
}

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

const baseTypography = {
  fontFamily: '"Inter", "Montserrat", "Roboto", sans-serif',
  h1: {
    fontWeight: 800,
    fontSize: '2.75rem',
    letterSpacing: '-0.04em',
  },
  h2: {
    fontWeight: 800,
    fontSize: '2.25rem',
    letterSpacing: '-0.04em',
  },
  h3: {
    fontWeight: 800,
    fontSize: '1.9rem',
    letterSpacing: '-0.03em',
  },
  h4: {
    fontWeight: 700,
    fontSize: '1.5rem',
    letterSpacing: '-0.02em',
  },
  h5: {
    fontWeight: 700,
    fontSize: '1.2rem',
    letterSpacing: '-0.01em',
  },
  h6: {
    fontWeight: 700,
    fontSize: '1rem',
    letterSpacing: '0.01em',
  },
  subtitle1: {
    fontWeight: 600,
    fontSize: '0.95rem',
    letterSpacing: '0.01em',
  },
  subtitle2: {
    fontWeight: 600,
    fontSize: '0.85rem',
    letterSpacing: '0.02em',
  },
  body1: {
    lineHeight: 1.7,
    fontSize: '0.98rem',
  },
  body2: {
    lineHeight: 1.6,
    fontSize: '0.875rem',
  },
  caption: {
    fontSize: '0.75rem',
    letterSpacing: '0.02em',
  },
  overline: {
    fontWeight: 700,
    fontSize: '0.7rem',
    letterSpacing: '0.2em',
  },
  button: {
    fontWeight: 700,
    textTransform: 'none',
    letterSpacing: '0.02em',
  },
};

// Dark Theme Configuration — zinc/neutral palette matching CSS vars
const darkTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: {
      main: '#1d4ed8',    // --sidebar-primary (interactive blue)
      light: '#3b82f6',
      dark: '#1e40af',
      contrastText: '#fafafa',
    },
    success: {
      main: '#10b981',    // --chart-2
    },
    warning: {
      main: '#f59e0b',    // --chart-3
    },
    error: {
      main: '#ef4444',    // --destructive-foreground
    },
    secondary: {
      main: '#27272a',    // --secondary
    },
    background: {
      default: '#0a0a0a', // --background
      paper: '#111111',   // slightly above card for visual depth
    },
    text: {
      primary: '#fafafa',     // --foreground
      secondary: '#a1a1aa',   // --muted-foreground
    },
    divider: '#27272a',       // --border
  },
  typography: baseTypography,
  shape: {
    borderRadius: 12,
  },
  components: {
    MuiCssBaseline: {
      styleOverrides: {
        body: {
          backgroundColor: 'var(--background)',
          color: 'var(--foreground)',
          scrollbarWidth: 'thin',
          textRendering: 'optimizeLegibility',
          fontFeatureSettings: '"ss01", "ss02", "cv01"',
          '&::-webkit-scrollbar': {
            width: '6px',
          },
          '&::-webkit-scrollbar-track': {
            background: 'var(--muted)',
          },
          '&::-webkit-scrollbar-thumb': {
            background: 'var(--border)',
            borderRadius: '10px',
            '&:hover': {
              background: 'var(--muted-foreground)',
            },
          },
        },
        '::selection': {
          backgroundColor: 'var(--accent)',
          color: 'var(--accent-foreground)',
        },
      },
    },
    MuiButtonBase: {
      defaultProps: {
        disableRipple: true,
      },
    },
    MuiIconButton: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          transition: 'transform 0.2s ease, background-color 0.2s ease',
          '&:hover': {
            transform: 'translateY(-1px)',
          },
        },
      },
    },
    MuiCard: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          backgroundColor: 'var(--card)',       // #0a0a0a
          color: 'var(--card-foreground)',       // #fafafa
          border: '1px solid var(--border)',     // #27272a
          boxShadow: '0 4px 16px rgba(0,0,0,0.5)',
          borderRadius: 16,
          '&:hover': {
            borderColor: 'var(--ring)',          // #52525b
            boxShadow: '0 6px 24px rgba(0,0,0,0.6)',
            transform: 'translateY(-2px)',
            transition: 'all 0.3s ease',
          },
        },
      },
    },
    MuiButton: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          padding: '8px 20px',
          transition: 'all 0.2s ease',
        },
        containedPrimary: {
          backgroundColor: 'var(--primary)',
          color: 'var(--primary-foreground)',
          '&:hover': {
            backgroundColor: 'var(--sidebar-primary)',
            boxShadow: '0 4px 12px hsla(var(--shadow-color) / var(--shadow-opacity))',
          },
        },
        outlinedPrimary: {
          borderColor: 'var(--border)',
          color: 'var(--primary)',
          '&:hover': {
            borderColor: 'var(--ring)',
            backgroundColor: 'var(--accent)',
          },
        },
      },
    },
    MuiPaper: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          backgroundColor: 'var(--card)',       // #0a0a0a
          color: 'var(--card-foreground)',       // #fafafa
          border: '1px solid var(--border)',     // #27272a
          borderRadius: 16,
          boxShadow: '0 4px 16px rgba(0,0,0,0.45)',
        },
      },
    },
    MuiDialog: {
      styleOverrides: {
        paper: {
          backgroundColor: 'var(--popover)',    // #0a0a0a
          color: 'var(--popover-foreground)',    // #fafafa
          borderRadius: 20,
          border: '1px solid var(--border)',     // #27272a
          backdropFilter: 'blur(12px)',
          boxShadow: '0 8px 40px rgba(0,0,0,0.6)',
        },
      },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: {
          backgroundColor: 'var(--popover)',
          color: 'var(--popover-foreground)',
          border: '1px solid var(--border)',
          borderRadius: 10,
          fontSize: '0.75rem',
          fontWeight: 600,
          padding: '6px 10px',
          boxShadow: '0 8px 24px hsla(var(--shadow-color) / var(--shadow-opacity))',
        },
      },
    },
    MuiChip: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          fontWeight: 600,
          letterSpacing: '0.02em',
        },
      },
    },
    MuiTableCell: {
      styleOverrides: {
        root: {
          borderColor: 'var(--border)',
        },
        head: {
          fontWeight: 700,
          fontSize: '0.7rem',
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
          color: 'var(--muted-foreground)',
        },
      },
    },
    MuiTableRow: {
      styleOverrides: {
        root: {
          transition: 'background-color 0.2s ease',
        },
      },
    },
    MuiTextField: {
      styleOverrides: {
        root: {
          '& .MuiOutlinedInput-root': {
            borderRadius: 10,
          },
        },
      },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: {
          borderRadius: 12,
          backgroundColor: 'var(--input)',      // #27272a
          color: 'var(--foreground)',            // #fafafa
          transition: 'border-color 0.2s ease, background-color 0.2s ease',
          '& fieldset': {
            borderColor: 'var(--border)',        // #27272a
          },
          '&:hover fieldset': {
            borderColor: 'var(--ring)',          // #52525b
          },
          '&.Mui-focused fieldset': {
            borderColor: '#1d4ed8',              // sidebar-primary blue
          },
        },
      },
    },
    MuiMenuItem: {
      styleOverrides: {
        root: {
          borderRadius: 8,
          margin: '2px 6px',
        },
      },
    },
    MuiDivider: {
      styleOverrides: {
        root: {
          borderColor: 'var(--border)',
        },
      },
    },
  },
});

// Light Theme Configuration — zinc/neutral palette matching CSS vars
const lightTheme = createTheme({
  palette: {
    mode: 'light',
    primary: {
      main: '#1976d2',          // standard MUI blue — used for value text & progress bars
      light: '#42a5f5',
      dark: '#1565c0',
      contrastText: '#ffffff',
    },
    success: {
      main: '#0d9488',          // --chart-2
    },
    warning: {
      main: '#f59e0b',          // --chart-5
    },
    error: {
      main: '#ef4444',          // --destructive
    },
    secondary: {
      main: '#71717a',          // --muted-foreground
    },
    background: {
      default: '#ffffff',       // --background
      paper: '#ffffff',         // --card
    },
    text: {
      primary: '#0a0a0a',       // --foreground
      secondary: '#71717a',     // --muted-foreground
    },
    divider: '#e4e4e7',         // --border
  },
  typography: baseTypography,
  shape: {
    borderRadius: 12,
  },
  components: {
    MuiCssBaseline: {
      styleOverrides: {
        body: {
          backgroundColor: '#ffffff',
          scrollbarWidth: 'thin',
          '&::-webkit-scrollbar': {
            width: '6px',
          },
          '&::-webkit-scrollbar-track': {
            background: '#f4f4f5',
          },
          '&::-webkit-scrollbar-thumb': {
            background: 'rgba(0, 0, 0, 0.2)',
            borderRadius: '10px',
            '&:hover': {
              background: 'rgba(0, 0, 0, 0.3)',
            },
          },
        },
      },
    },
    MuiCard: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          backgroundColor: 'var(--card)',       // #ffffff
          color: 'var(--card-foreground)',       // #0a0a0a
          border: '1px solid var(--border)',     // #e4e4e7
          boxShadow: '0 1px 4px rgba(0,0,0,0.07), 0 1px 2px rgba(0,0,0,0.04)',
          borderRadius: 16,
          transition: 'all 0.3s ease',
          '&:hover': {
            borderColor: 'var(--ring)',          // #d4d4d8
            boxShadow: '0 4px 14px rgba(0,0,0,0.10)',
            transform: 'translateY(-2px)',
          },
        },
      },
    },
    MuiIconButton: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          transition: 'transform 0.2s ease, background-color 0.2s ease',
          '&:hover': {
            transform: 'translateY(-1px)',
          },
        },
      },
    },
    MuiButton: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          padding: '8px 20px',
          transition: 'all 0.2s ease',
        },
        containedPrimary: {
          backgroundColor: '#1976d2',
          color: '#ffffff',
          '&:hover': {
            backgroundColor: '#1565c0',
            boxShadow: '0 4px 12px rgba(25, 118, 210, 0.3)',
          },
        },
        outlinedPrimary: {
          borderColor: 'rgba(25, 118, 210, 0.5)',
          color: '#1976d2',
          '&:hover': {
            borderColor: '#1976d2',
            backgroundColor: 'rgba(25, 118, 210, 0.08)',
          },
        },
      },
    },
    MuiPaper: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          backgroundColor: 'var(--card)',       // #ffffff
          color: 'var(--card-foreground)',       // #0a0a0a
          border: '1px solid var(--border)',     // #e4e4e7
          borderRadius: 16,
          boxShadow: '0 1px 4px rgba(0,0,0,0.07)',
        },
      },
    },
    MuiDialog: {
      styleOverrides: {
        paper: {
          backgroundColor: 'var(--popover)',    // #ffffff
          color: 'var(--popover-foreground)',    // #0a0a0a
          borderRadius: 20,
          border: '1px solid var(--border)',     // #e4e4e7
          backdropFilter: 'blur(8px)',
          boxShadow: '0 8px 32px rgba(0,0,0,0.12)',
        },
      },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: {
          backgroundColor: 'rgba(255, 255, 255, 0.95)',
          border: '1px solid rgba(0, 0, 0, 0.08)',
          color: '#111111',
          borderRadius: 10,
          fontSize: '0.75rem',
          fontWeight: 600,
          padding: '6px 10px',
          boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
        },
      },
    },
    MuiChip: {
      styleOverrides: {
        root: {
          borderRadius: 10,
          fontWeight: 600,
          letterSpacing: '0.02em',
        },
      },
    },
    MuiTableCell: {
      styleOverrides: {
        root: {
          borderColor: 'rgba(0, 0, 0, 0.08)',
        },
        head: {
          fontWeight: 700,
          fontSize: '0.7rem',
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
          color: 'rgba(0, 0, 0, 0.6)',
        },
      },
    },
    MuiTableRow: {
      styleOverrides: {
        root: {
          transition: 'background-color 0.2s ease',
        },
      },
    },
    MuiTextField: {
      styleOverrides: {
        root: {
          '& .MuiOutlinedInput-root': {
            borderRadius: 10,
            backgroundColor: 'rgba(0, 0, 0, 0.02)',
            '& fieldset': {
              borderColor: 'rgba(0, 0, 0, 0.12)',
            },
            '&:hover fieldset': {
              borderColor: 'rgba(0, 0, 0, 0.2)',
            },
            '&.Mui-focused fieldset': {
              borderColor: '#1976d2',
            },
          },
        },
      },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: {
          borderRadius: 12,
          backgroundColor: 'var(--input)',      // #d4d4d8 tint
          color: 'var(--foreground)',
          transition: 'border-color 0.2s ease, background-color 0.2s ease',
          '& fieldset': {
            borderColor: 'var(--border)',        // #e4e4e7
          },
          '&:hover fieldset': {
            borderColor: 'var(--ring)',          // #d4d4d8
          },
          '&.Mui-focused fieldset': {
            borderColor: 'var(--primary)',       // #18181b
          },
        },
      },
    },
    MuiMenuItem: {
      styleOverrides: {
        root: {
          borderRadius: 8,
          margin: '2px 6px',
        },
      },
    },
    MuiDivider: {
      styleOverrides: {
        root: {
          borderColor: 'var(--border)',          // #e4e4e7
        },
      },
    },
  },
});

export const ThemeProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  // Load theme from localStorage or default to 'dark'
  const [mode, setModeState] = useState<ThemeMode>(() => {
    const saved = localStorage.getItem('theme-mode');
    return (saved === 'light' || saved === 'dark') ? saved : 'dark';
  });

  // Apply dark class on initial mount
  useEffect(() => {
    const root = document.documentElement;
    const saved = localStorage.getItem('theme-mode');
    const initialMode = (saved === 'light' || saved === 'dark') ? saved : 'dark';
    if (initialMode === 'dark') {
      root.classList.add('dark');
    } else {
      root.classList.remove('dark');
    }
  }, []);

  // Save to localStorage whenever mode changes (dark class is managed by toggleTheme directly)
  useEffect(() => {
    localStorage.setItem('theme-mode', mode);
  }, [mode]);

  const toggleTheme = () => {
    const root = document.documentElement;

    // Step 1 — add theme-switching so all elements get their transition property
    root.classList.add('theme-switching');

    // Step 2 — double rAF: guarantees the browser has painted one frame with the
    // transition rules active before the CSS variables change.
    // Without this, both classList changes land in the same paint frame and the
    // browser skips the transition (elements snap instead of animating).
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        // Toggle the .dark class → changes all CSS variables → triggers transitions
        const next = root.classList.contains('dark') ? 'light' : 'dark';
        if (next === 'dark') {
          root.classList.add('dark');
        } else {
          root.classList.remove('dark');
        }

        // Sync React state so components that read `mode` re-render
        setModeState(next);

        // Remove theme-switching after page transition finishes
        setTimeout(() => root.classList.remove('theme-switching'), 400);
      });
    });
  };

  const setMode = (newMode: ThemeMode) => {
    setModeState(newMode);
  };

  const theme = useMemo(() => {
    return mode === 'light' ? lightTheme : darkTheme;
  }, [mode]);

  return (
    <ThemeContext.Provider value={{ mode, toggleTheme, setMode, theme }}>
      {children}
    </ThemeContext.Provider>
  );
};

export const useTheme = () => {
  const context = useContext(ThemeContext);
  if (context === undefined) {
    throw new Error('useTheme must be used within a ThemeProvider');
  }
  return context;
};
