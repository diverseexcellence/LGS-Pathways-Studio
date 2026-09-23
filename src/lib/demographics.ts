// Source-system codes confirmed for hand entry. The stored value is the code; the label is
// what staff see in the dropdown.

export interface CodedOption {
  value: string;
  label: string;
}

const NOT_RECORDED: CodedOption = { value: '', label: 'Not recorded' };

export const GENDER_OPTIONS: CodedOption[] = [
  NOT_RECORDED,
  { value: 'U', label: 'U' },
  { value: 'F', label: 'F' },
  { value: 'M', label: 'M' },
];

export const ETHNICITY_OPTIONS: CodedOption[] = [
  NOT_RECORDED,
  { value: 'Y', label: 'Y' },
  { value: 'N', label: 'N' },
];

export const RACE_OPTIONS: CodedOption[] = [
  NOT_RECORDED,
  { value: '1', label: '1 – American Indian or Alaskan Native' },
  { value: '2', label: '2 – Black or African American' },
  { value: '3', label: '3 – Asian' },
  { value: '5', label: '5 – White' },
  { value: '7', label: '7 – Native Hawaiian/Other Pac Islander' },
];

export const ENROLLMENT_OPTIONS: CodedOption[] = [
  { value: 'Enrolled', label: 'Enrolled' },
  { value: 'Unenrolled', label: 'Unenrolled' },
];

export const LUNCH_OPTIONS: CodedOption[] = [
  { value: '', label: '(no status)' },
  { value: 'P', label: 'Full pay (P)' },
  { value: 'R', label: 'Reduced (R)' },
  { value: 'F', label: 'Free (F)' },
  { value: 'E', label: 'Exempt (E)' },
  { value: 'T', label: 'Temporary (T)' },
  { value: 'FDC', label: 'Free-DC (FDC)' },
  { value: 'RDC', label: 'Reduced-DC (RDC)' },
];

export const ELL_OPTIONS: CodedOption[] = [
  NOT_RECORDED,
  { value: 'Y', label: 'Y' },
  { value: 'N', label: 'N' },
];

export const TRUE_FALSE_OPTIONS: CodedOption[] = [
  NOT_RECORDED,
  { value: 'T', label: 'T' },
  { value: 'F', label: 'F' },
];

/** Keep a stored value selectable when it predates this list, instead of showing a blank control. */
export function optionsWithCurrent(options: CodedOption[], current: string | null | undefined): CodedOption[] {
  if (!current || options.some(o => o.value === current)) return options;
  return [...options, { value: current, label: current }];
}

export function optionLabel(options: CodedOption[], value: string | null | undefined): string {
  if (!value) return '';
  return options.find(o => o.value === value)?.label ?? value;
}
