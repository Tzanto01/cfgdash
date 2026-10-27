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

/** Error bodies of api-v1. */
export interface ApiError {
  error: string;
  reason?: string;
}
