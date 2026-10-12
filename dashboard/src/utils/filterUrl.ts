import {
  SECTION2_PARAM_KEYS,
  type FilterRequest, type FilterRequestInfo, type PeriodLabel, type Section2ParamKey,
} from '../types';

/**
 * The applied Section 2 filter, carried in the dashboard's URL.
 *
 * Each browser tab keeps its own filter: in React state and in its own address bar, never on the
 * server. Two people (or two tabs) filtering at the same time therefore cannot affect each other,
 * and a refresh keeps the filter. Example:
 *
 *   /dashboard?filter=time&period=day&start=…&end=…&result=124
 *   /dashboard?filter=cycle&cycles=1-104&params=energy_kwh_total,cycle_count&result=125
 *   /dashboard?filter=item&item=Aluminium&result=126
 *
 * `result` is the id of the saved calculation (calculation_requests). On a refresh the page loads
 * that saved result by id instead of computing it again; the other parameters only describe it —
 * when the result can be loaded, the server's copy of the request is what the page shows. They are
 * used on their own only to refill the filter bar when the saved result no longer exists.
 *
 * `params` lists the parameter toggles; left out, it means all of them. "item" is the word users
 * see, so the URL says item; the API and database still say metal.
 */

const PERIODS: readonly PeriodLabel[] = ['hour', 'shift', 'day', 'week', 'month', 'year'];

export interface FilterFromUrl {
  /** The filter the URL describes, or null when it describes none (or nothing valid). */
  request: FilterRequest | null;
  /** The saved result to reopen, or null. */
  resultId: number | null;
}

export function filterToSearchParams(req: FilterRequest, resultId: number): URLSearchParams {
  const p = new URLSearchParams();

  if (req.filterBy === 'time') {
    p.set('filter', 'time');
    if (req.periodLabel) p.set('period', req.periodLabel);
    p.set('start', req.filterStart);
    p.set('end', req.filterEnd);
  } else if (req.filterBy === 'cycle') {
    p.set('filter', 'cycle');
    p.set('cycles', `${req.filterCycleFrom}-${req.filterCycleTo}`);
  } else {
    p.set('filter', 'item');
    p.set('item', req.filterMetalName ?? '');
  }

  const selected = req.selectedParameters ?? [];
  if (selected.length > 0 && selected.length < SECTION2_PARAM_KEYS.length) p.set('params', selected.join(','));

  p.set('result', String(resultId));
  return p;
}

export function filterFromSearchParams(p: URLSearchParams): FilterFromUrl {
  const idText = p.get('result');
  const id = idText && /^\d{1,9}$/.test(idText) ? Number(idText) : null;
  return { request: requestFromParams(p), resultId: id && id > 0 ? id : null };
}

function requestFromParams(p: URLSearchParams): FilterRequest | null {
  const selectedParameters = parseParams(p.get('params'));
  const now = new Date().toISOString();

  switch (p.get('filter')) {
    case 'time': {
      const start = p.get('start');
      const end = p.get('end');
      if (!start || !end || isNaN(Date.parse(start)) || isNaN(Date.parse(end))) return null;
      const period = p.get('period') as PeriodLabel | null;
      return {
        filterBy: 'time', filterStart: start, filterEnd: end,
        periodLabel: period && PERIODS.includes(period) ? period : null,
        selectedParameters,
      };
    }
    case 'cycle': {
      const m = /^(\d+)-(\d+)$/.exec(p.get('cycles') ?? '');
      if (!m) return null;
      return {
        filterBy: 'cycle', filterStart: now, filterEnd: now,
        filterCycleFrom: Number(m[1]), filterCycleTo: Number(m[2]), selectedParameters,
      };
    }
    case 'item': {
      const item = p.get('item')?.trim();
      if (!item) return null;
      return { filterBy: 'metal', filterStart: now, filterEnd: now, filterMetalName: item, selectedParameters };
    }
    default:
      return null;
  }
}

function parseParams(text: string | null): Section2ParamKey[] {
  if (!text) return [];
  const known = new Set<string>(SECTION2_PARAM_KEYS);
  return text.split(',').filter((k): k is Section2ParamKey => known.has(k));
}

/** The server's saved copy of a request, as the filter the page shows. */
export function requestFromInfo(info: FilterRequestInfo): FilterRequest {
  const known = new Set<string>(SECTION2_PARAM_KEYS);
  return {
    filterBy:           info.filterBy,
    filterStart:        info.filterStart,
    filterEnd:          info.filterEnd,
    periodLabel:        info.periodLabel,
    filterCycleFrom:    info.filterCycleFrom,
    filterCycleTo:      info.filterCycleTo,
    filterMetalName:    info.filterMetalName,
    // null on the server means "all were computed", which the page writes as an empty list.
    selectedParameters: (info.selectedParameters ?? []).filter((k): k is Section2ParamKey => known.has(k)),
  };
}

/** An ISO time as the value a `datetime-local` input expects, in this browser's time zone. */
export function toLocalInputValue(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export { PERIODS };
