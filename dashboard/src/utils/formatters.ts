type DateInput = Date | string | number | null | undefined;

const toDate = (value: DateInput): Date | null => {
  if (!value) return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
};

export const formatDate = (
  value: DateInput,
  options: Intl.DateTimeFormatOptions = { year: 'numeric', month: 'short', day: '2-digit' }
): string => {
  const date = toDate(value);
  if (!date) return 'N/A';
  return new Intl.DateTimeFormat(undefined, options).format(date);
};

export const formatTime = (
  value: DateInput,
  options: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' }
): string => {
  const date = toDate(value);
  if (!date) return 'N/A';
  return new Intl.DateTimeFormat(undefined, options).format(date);
};

export const formatDateTime = (
  value: DateInput,
  options: Intl.DateTimeFormatOptions = {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }
): string => {
  const date = toDate(value);
  if (!date) return 'N/A';
  return new Intl.DateTimeFormat(undefined, options).format(date);
};

export const formatNumber = (
  value: number | string | null | undefined,
  options?: Intl.NumberFormatOptions
): string => {
  if (value === null || value === undefined || value === '') return '0';
  const numberValue = typeof value === 'number' ? value : Number(value);
  if (Number.isNaN(numberValue)) return String(value);
  return new Intl.NumberFormat(undefined, options).format(numberValue);
};
