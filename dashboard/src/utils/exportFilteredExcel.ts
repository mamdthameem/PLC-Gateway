import * as XLSX from 'xlsx';
import { PARAM_META, formatParameterValue } from './unitConverters';
import type {
  FilterResult, FilteredCycle, FilteredMetalProduction, ShotsBreakdownEntry,
} from '../types';

/**
 * Builds the filtered-results workbook from data already on screen.
 *
 * Only WRITES a workbook — no spreadsheet file is ever parsed. That matters because the pinned
 * xlsx build's known advisories are all in the reader path, which this never exercises.
 */

export interface FilteredExportInput {
  label: string;
  filterBy: 'time' | 'cycle' | 'metal';
  filterStart: string;
  filterEnd: string;
  results: FilterResult[];
  cycles: FilteredCycle[];
  metals: FilteredMetalProduction[];
  shots: ShotsBreakdownEntry[];
}

function localStamp(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toLocaleString();
}

/** Excel caps sheet names at 31 chars and rejects several punctuation marks. */
function safeSheetName(name: string): string {
  return name.replace(/[\\/?*[\]:]/g, '-').slice(0, 31);
}

function fileStamp(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`;
}

export function exportFilteredWorkbook(input: FilteredExportInput): void {
  const { label, filterBy, filterStart, filterEnd, results, cycles, metals, shots } = input;

  const wb = XLSX.utils.book_new();

  // Sheet 1 — scalar parameters, with both the raw stored value and the formatted display value
  // so the export is useful for both further analysis and reading as-is.
  const paramRows = [
    ['Filter', label],
    ['Filter mode', filterBy],
    ...(filterBy === 'time'
      ? [['Range', `${localStamp(filterStart)} → ${localStamp(filterEnd)}`]]
      : []),
    ['Exported', new Date().toLocaleString()],
    [],
    ['Parameter', 'Raw value', 'Display value'],
    ...results.map(r => [
      PARAM_META[r.parameterName]?.label ?? r.parameterName,
      r.value,
      formatParameterValue(r.parameterName, r.value),
    ]),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(paramRows), 'Parameters');

  // Sheet 2 — production per casting metal (summed declared weights).
  const metalTotal = metals.reduce((sum, m) => sum + m.productionKg, 0);
  const metalRows = [
    ['Casting metal', 'Declared weight (kg)'],
    ...metals.map(m => [m.metalName, m.productionKg]),
    ...(metals.length > 0 ? [['Total', metalTotal]] : []),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(metalRows), 'Metal Production');

  // Sheet 3 — per-cycle breakdown.
  const cycleRows = [
    [
      'Cycle #', 'Start', 'End',
      'Metal 1', 'Metal 1 kg', 'Metal 2', 'Metal 2 kg',
      'Metal 3', 'Metal 3 kg', 'Metal 4', 'Metal 4 kg',
      'Production (kg)', 'Energy (kWh)', 'Shots usage',
    ],
    ...cycles.map(c => [
      c.cycleNumber,
      localStamp(c.blastStart),
      localStamp(c.blastEnd),
      c.metal1Name ?? '', c.metal1WeightKg ?? '',
      c.metal2Name ?? '', c.metal2WeightKg ?? '',
      c.metal3Name ?? '', c.metal3WeightKg ?? '',
      c.metal4Name ?? '', c.metal4WeightKg ?? '',
      c.productionKg,
      c.energyKwh,
      c.shotsUsage,
    ]),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(cycleRows), 'Cycles');

  // Sheet 4 — blast cycles per refill interval.
  const shotsRows = [
    ['Refill timestamp', 'Blast cycles until next refill'],
    ...shots.map(s => [localStamp(s.refillTimestamp), s.blastCount]),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(shotsRows), safeSheetName('Shots Breakdown'));

  XLSX.writeFile(wb, `plc-filtered-${fileStamp()}.xlsx`);
}
