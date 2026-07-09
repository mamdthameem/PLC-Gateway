type DateInput = Date | string | number | null | undefined;

const toDate = (value: DateInput): Date | null => {
  if (!value) return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
};

/** End of the given date (23:59:59.999). Used so "valid until X" means through end of day X. */
export const endOfDay = (value: DateInput): Date | null => {
  const date = toDate(value);
  if (!date) return null;
  return new Date(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59, 999);
};

/** True if the user's validUntil date has passed (after end of that day). */
export const isUserExpired = (validUntil: Date | undefined | null): boolean => {
  if (!validUntil) return false;
  const eod = endOfDay(validUntil);
  return eod ? new Date() > eod : false;
};

/** Days until date (can be negative if in the past). Uses calendar day difference. */
export const daysUntil = (value: DateInput): number | null => {
  const date = toDate(value);
  if (!date) return null;
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return Math.ceil((d.getTime() - today.getTime()) / (24 * 60 * 60 * 1000));
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
