import {
  Bot,
  Boxes,
  CalendarClock,
  Database,
  FileText,
  LayoutGrid,
  Plug,
  SlidersHorizontal,
  Sparkles,
  Terminal,
  Variable,
  Zap,
  type LucideIcon
} from 'lucide-react';
import type { Block, CollectorStatus, KvBlock, Section, Snapshot, TableBlock, TreeBlock, TreeNode } from './models';

/** Sidebar group of a view; `other` renders without a label. */
export type ViewGroup = 'machine' | 'harness' | 'activity' | 'other';

export interface ViewDefinition {
  id: string;
  label: string;
  description: string;
  group: ViewGroup;
  icon: LucideIcon;
}

/** Known view ids in navigation order (snapshot-v1), then `other`. */
export const VIEW_DEFINITIONS: readonly ViewDefinition[] = [
  { id: 'overview', label: 'Overview', description: 'What this machine is, what is active and where to look next.', group: 'machine', icon: LayoutGrid },
  { id: 'settings', label: 'Settings', description: 'Effective Claude Code settings for the folder and where each value comes from.', group: 'harness', icon: SlidersHorizontal },
  { id: 'instructions', label: 'Instructions', description: 'Instruction files loaded for the folder, in load order.', group: 'harness', icon: FileText },
  { id: 'agents', label: 'Agents', description: 'Agent definitions and the company agent cores.', group: 'harness', icon: Bot },
  { id: 'skills', label: 'Skills & commands', description: 'Skills, slash commands and output styles.', group: 'harness', icon: Sparkles },
  { id: 'hooks', label: 'Hooks', description: 'Hooks registered for the folder and the scripts they run.', group: 'harness', icon: Zap },
  { id: 'mcp-permissions', label: 'MCP & permissions', description: 'MCP servers, permission rules and model settings.', group: 'harness', icon: Plug },
  { id: 'env', label: 'Environment', description: 'Harness environment variables and where they are set.', group: 'harness', icon: Variable },
  { id: 'jobs', label: 'Jobs', description: 'Scheduled jobs that run harness scripts.', group: 'harness', icon: CalendarClock },
  { id: 'sessions', label: 'Sessions', description: 'Recent Claude Code sessions with tokens, estimated cost and audit signals.', group: 'activity', icon: Terminal },
  { id: 'memory', label: 'Memory', description: 'What agents remember in the Engram store on this machine: projects, topic keys and observations.', group: 'activity', icon: Database },
  { id: 'other', label: 'Other', description: 'Data from sources this version of the UI has no view for.', group: 'other', icon: Boxes }
];

/** Sidebar group labels in display order; `other` has none. */
export const VIEW_GROUPS: readonly { id: ViewGroup; label?: string }[] = [
  { id: 'machine', label: 'Machine' },
  { id: 'harness', label: 'Harness' },
  { id: 'activity', label: 'Activity' },
  { id: 'other' }
];

export const KNOWN_VIEW_IDS = new Set(VIEW_DEFINITIONS.filter((v) => v.id !== 'other').map((v) => v.id));

/** The nav view a snapshot view id belongs to: itself when known, else `other`. */
export function navViewId(view: string): string {
  return KNOWN_VIEW_IDS.has(view) ? view : 'other';
}

export interface ViewModel extends ViewDefinition {
  collectors: CollectorStatus[];
  sections: Section[];
  itemCount: number;
  hasUnavailable: boolean;
  /** Shown in the nav: overview always, a view with an active or unavailable collector, other with unknown ids. */
  visible: boolean;
}

export function buildViews(snapshot: Snapshot): ViewModel[] {
  return VIEW_DEFINITIONS.map((definition) => {
    const collectors = snapshot.collectors.filter((c) => navViewId(c.view) === definition.id);
    const sections = snapshot.sections.filter((s) => navViewId(s.view) === definition.id);
    const visible =
      definition.id === 'overview' ||
      (definition.id === 'other'
        ? collectors.length > 0 || sections.length > 0
        : collectors.some((c) => c.state !== 'notPresent'));
    return {
      ...definition,
      collectors,
      sections,
      itemCount: sections.reduce((sum, section) => sum + sectionItemCount(section), 0),
      hasUnavailable: collectors.some((c) => c.state === 'unavailable'),
      visible
    };
  });
}

export function sectionItemCount(section: Section): number {
  return section.blocks.reduce((sum, block) => sum + blockItemCount(block), 0);
}

function blockItemCount(block: Block): number {
  switch (block.type) {
    case 'kv':
      return (block as KvBlock).rows?.length ?? 0;
    case 'table':
      return (block as TableBlock).rows?.length ?? 0;
    case 'tree':
      return countNodes((block as TreeBlock).nodes ?? []);
    default:
      return 0;
  }
}

function countNodes(nodes: TreeNode[]): number {
  return nodes.reduce((sum, node) => sum + 1 + countNodes(node.children ?? []), 0);
}

export function stateLabel(state: string): string {
  switch (state) {
    case 'active':
      return 'active';
    case 'unavailable':
      return 'unavailable';
    case 'notPresent':
      return 'not on this machine';
    default:
      return state;
  }
}
