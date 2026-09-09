import * as XLSX from 'xlsx';
import { PARAM_META, formatParameterValue } from './unitConverters';
import type { FilterResult, FilteredCycle, FilteredMetalProduction } from '../types';

/**
 * Builds the filtered-results workbook.
 *
 * Reads the FETCHED Section 2 dataset, never a rendered table — which is why removing Section 2's
 * tables left the export intact. The three arrays here are the same ones FilterResultsView fetches
 * to drive its tiles and graphs.
 *
 * Only WRITES a workbook — no spreadsheet file is ever parsed. That matters because the pinned
 * xlsx build's known advisories are all in the reader path, which this never exercises.
 *
 * Sheets: Parameters, Item Production, Cycles. The former "Shots Breakdown" sheet was dropped when
 * the shots breakdown became Section 1 only — it does not respond to a filter, so there was
 * nothing filter-scoped left to export.
 *
 * A sheet is empty when its parameter was not selected in the filter bar: unselected parameters
 * are never computed, so there is no data to export for them.
 */

export interface FilteredExportInput {
  label: string;
  filterBy: 'time' | 'cycle' | 'metal';
  filterStart: string;
  filterEnd: string;
  /** Set only for an item filter. */
  itemName: string | null;
  results: FilterResult[];
  cycles: FilteredCycle[];
  items: FilteredMetalProduction[];
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
  const { label, filterBy, filterStart, filterEnd, itemName, results, cycles, items } = input;

  const wb = XLSX.utils.book_new();

  // Sheet 1 — scalar parameters, with both the raw stored value and the formatted display value
  // so the export is useful for both further analysis and reading as-is.
  const paramRows = [
    ['Filter', label],
    // "item" is the user-facing word for what the wire format calls a metal filter.
    ['Filter mode', filterBy === 'metal' ? 'item' : filterBy],
    ...(itemName ? [['Casting item', itemName]] : []),
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

  // Sheet 2 — production per casting item (summed declared weights).
  const itemTotal = items.reduce((sum, i) => sum + i.productionKg, 0);
  const itemRows = [
    ['Casting item', 'Declared weight (kg)'],
    ...items.map(i => [i.metalName, i.productionKg]),
    ...(items.length > 0 ? [['Total', itemTotal]] : []),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(itemRows), 'Item Production');

  // Sheet 3 — per-cycle breakdown. The four declared slots keep their per-cycle detail here even
  // though Section 2 no longer renders a cycle table on screen.
  const cycleRows = [
    [
      'Cycle #', 'Start', 'End',
      'Item 1', 'Item 1 kg', 'Item 2', 'Item 2 kg',
      'Item 3', 'Item 3 kg', 'Item 4', 'Item 4 kg',
      'Production (kg)', 'Energy (kWh)',
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
    ]),
  ];
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(cycleRows), safeSheetName('Cycles'));

  XLSX.writeFile(wb, `plc-filtered-${fileStamp()}.xlsx`);
}
