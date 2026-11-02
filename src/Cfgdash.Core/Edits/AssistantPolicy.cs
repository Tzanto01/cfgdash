namespace Cfgdash.Core.Edits;

/// <summary><c>Free</c> allows any valid value; <c>TightenOnly</c> allows only <see cref="AssistantPolicy.Direction"/>.</summary>
internal enum AssistantKind { Free, TightenOnly }

/// <summary>The safer direction of a tighten-only key: an exact value (<c>True</c>, <c>False</c>, <c>Disable</c>) or a list rule.</summary>
internal enum AssistantDirection { None, True, False, Subset, Superset, Disable }

/// <summary>What the config assistant may propose for a catalog key (docs/contracts/assistant-v1.md); a null policy means deny.</summary>
internal sealed record AssistantPolicy(AssistantKind Kind, AssistantDirection Direction = AssistantDirection.None, bool UserScope = false);
