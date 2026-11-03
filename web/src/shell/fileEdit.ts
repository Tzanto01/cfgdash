/** Why a file is not editable (edits-v2), in words. */
export const NOT_EDITABLE: Record<string, string> = {
  'not-editable-kind': 'Only instruction files, agents, skills, commands and Claude Code settings files can be edited here.',
  company: 'Company files are read-only in cfgdash.',
  'outside-harness-roots': 'This file is outside the folders cfgdash edits (its config folder, this folder and its parents up to the workspace, and the config repo).',
  unreadable: "cfgdash couldn't read this file.",
  'too-large': 'This file is larger than 256 KB.',
  'not-text': "This file isn't plain UTF-8 text.",
  'parse-error': "This settings file isn't valid JSON, so cfgdash won't edit it. Fix it by hand first.",
  'contains-redacted-values': 'This file holds values cfgdash hides (such as secrets), and cfgdash only has the hidden version, so it can\'t be edited here.'
};
