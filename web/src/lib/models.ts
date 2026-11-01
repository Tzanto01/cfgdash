/** Any JSON value, as carried by kv values, contributions, table cells and tree meta. */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type OsFamily = 'windows' | 'linux' | 'macos';
export type CollectorState = 'active' | 'notPresent' | 'unavailable';
export type ContributionStatus = 'applied' | 'overridden' | 'merged';
export type FileKind =
  | 'instruction'
  | 'settings'
  | 'agent'
  | 'skill'
  | 'command'
  | 'outputStyle'
  | 'hookScript'
  | 'mcp'
  | 'other';

/** Snapshot schema v1, see docs/contracts/snapshot-v1.md. Null fields are omitted, so they are optional here. */
export interface Snapshot {
  schemaVersion: number;
  machine: MachineInfo;
  context: SnapshotContext;
  collectors: CollectorStatus[];
  sections: Section[];
  files: FileRef[];
}

export interface MachineInfo {
  hostname: string;
  tailnet?: TailnetInfo;
  os: OsInfo;
  collectedAt: string;
  cfgdashVersion: string;
}

export interface TailnetInfo {
  dnsName: string;
  tailnetName: string;
  tags: string[];
}

export interface OsInfo {
  family: OsFamily;
  description: string;
  arch: string;
}

export interface SnapshotContext {
  targetDir: string;
  paths: ContextPath[];
}

export interface ContextPath {
  key: string;
  value?: string | null;
  source?: string | null;
  exists: boolean;
}

export interface Requirements {
  os?: OsFamily[];
  tools?: string[];
  paths?: string[];
  targetDir?: boolean;
}

export interface CollectorStatus {
  id: string;
  title: string;
  version: string;
  view: string;
  state: CollectorState;
  reason?: string;
  requirements: Requirements;
  durationMs: number;
  notes?: string[];
}

export interface Section {
  collectorId: string;
  view: string;
  title: string;
  blocks: Block[];
}

export interface BlockBase {
  type: string;
  id: string;
  title: string;
  description?: string;
}

export interface KvBlock extends BlockBase {
  type: 'kv';
  rows: KvRow[];
}

export interface KvRow {
  key: string;
  value?: JsonValue;
  source?: SourceRef;
  redacted?: boolean;
  flags?: string[];
  contributions?: Contribution[];
}

export interface SourceRef {
  label: string;
  fileId?: string;
}

export interface Contribution {
  value?: JsonValue;
  source: SourceRef;
  status: ContributionStatus;
}

export interface TableBlock extends BlockBase {
  type: 'table';
  columns: TableColumn[];
  rows: TableRow[];
}

export interface TableColumn {
  key: string;
  label: string;
}

/** A link cell; plain cells are strings, numbers, booleans or null. */
export interface LinkCell {
  text: string;
  fileId?: string;
}

export type Cell = string | number | boolean | null | LinkCell;

export interface TableRow {
  id: string;
  cells: Record<string, Cell | undefined>;
  flags?: string[];
  fileId?: string;
  detailId?: string;
}

export interface TreeBlock extends BlockBase {
  type: 'tree';
  nodes: TreeNode[];
  summary?: TreeSummary;
}

export interface TreeNode {
  id: string;
  label: string;
  children: TreeNode[];
  fileId?: string;
  detailId?: string;
  badges?: string[];
  meta?: Record<string, JsonValue>;
}

export interface TreeSummary {
  files: number;
  sizeBytes: number;
  tokens: number;
}

/** Any block, including types this UI does not know yet. */
export type Block = KvBlock | TableBlock | TreeBlock | BlockBase;

export interface FileRef {
  id: string;
  path: string;
  kind: FileKind | string;
  sizeBytes: number;
  tokens: number;
  company: boolean;
}

/** `GET /api/v1/files/{id}`. */
export interface FileContent {
  id: string;
  path: string;
  kind: FileKind | string;
  contentType: 'markdown' | 'json' | 'text';
  content: string;
  truncated: boolean;
}

/** `GET /api/v1/details/{detailId}`. */
export interface DetailContent {
  id: string;
  title: string;
  contentType: 'markdown' | 'json' | 'text';
  content: string;
  truncated: boolean;
}

export interface SearchHit {
  kind: 'row' | 'node' | 'file';
  view: string;
  collectorId: string;
  blockId: string;
  itemId?: string;
  fileId?: string;
  line?: number;
  snippet: string;
}

/** `GET /api/v1/search`. */
export interface SearchResponse {
  query: string;
  results: SearchHit[];
  truncated: boolean;
}

export interface DirEntry {
  name: string;
  path: string;
}

export interface DirSuggestion {
  label: string;
  path: string;
}

/** `GET /api/v1/dirs`. */
export interface DirsResponse {
  path: string;
  parent?: string | null;
  entries: DirEntry[];
  suggestions: DirSuggestion[];
}

export type Severity = 'critical' | 'warning' | 'info';

/** Where a finding's evidence lives in the snapshot (health-v1). */
export interface FindingEvidence {
  collectorId: string;
  blockId?: string;
  itemId?: string;
  key?: string;
  fileId?: string;
}

export interface Finding {
  id: string;
  rule: string;
  severity: Severity;
  title: string;
  detail: string;
  evidence: FindingEvidence;
  view: string;
}

export interface SeverityCounts {
  critical: number;
  warning: number;
  info: number;
}

/** `GET /api/v1/findings`. */
export interface FindingsResponse {
  dir: string;
  collectedAt: string;
  counts: SeverityCounts;
  findings: Finding[];
}

/** `GET /api/v1/health`. */
export interface HealthInfo {
  status: string;
  version: string;
  schemaVersion: number;
  edits?: boolean;
}

export type EditScope = 'user' | 'project' | 'local' | 'machine';

/** One catalog row of `GET /api/v2/settings`; values are redacted by the server. */
export interface SettingRow {
  key: string;
  label: string;
  description: string;
  type: 'bool' | 'enum' | 'string' | 'number' | 'stringList' | 'json' | string;
  options?: string[];
  scopes: EditScope[];
  values: Partial<Record<EditScope, JsonValue | null>>;
  effective: JsonValue | null;
  effectiveSource: EditScope | 'merged' | string | null;
  editable: boolean;
  /** Why the row is read-only: `secret`, `unsafe-env`, `not-in-catalog`, `scope-unavailable`, `unsafe-target`. */
  reason?: string;
}

export interface SettingsCategory {
  id: string;
  label: string;
  settings: SettingRow[];
}

export interface SettingsFile {
  scope: EditScope;
  path: string;
  exists: boolean;
  gitTracked: boolean;
}

/** `GET /api/v2/settings`. */
export interface SettingsResponse {
  categories: SettingsCategory[];
  files: SettingsFile[];
}

export type SettingChange = { key: string; scope: EditScope; value: JsonValue } | { key: string; scope: EditScope; unset: true };

export interface ProposalFile {
  path: string;
  scope: EditScope;
  sha256Before: string | null;
  /** Unified diff of the redacted before and after text; may start with `# cfgdash: file will be reformatted`. */
  diff: string;
}

/** `POST /api/v2/proposals`. */
export interface Proposal {
  id: string;
  expiresAt: string;
  files: ProposalFile[];
  confirm: string;
}

/** `POST /api/v2/proposals/{id}/apply`. */
export interface ApplyResult {
  applied: boolean;
  auditId: string;
}

/** Error bodies of api-v1. */
export interface ApiError {
  error: string;
  reason?: string;
}
