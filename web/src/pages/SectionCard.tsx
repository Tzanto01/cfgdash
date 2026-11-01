import { Block } from '../blocks/Block';
import type { RowFilter } from '../blocks/filter';
import type { CollectorStatus, Section } from '../lib/models';
import type { RevealTarget } from './useReveal';

/** One section as a card: header, collector notes, blocks. */
export function SectionCard({
  section,
  collector,
  headingLevel = 2,
  reveal,
  filter
}: {
  section: Section;
  collector?: CollectorStatus;
  headingLevel?: 2 | 3;
  reveal: RevealTarget | null;
  filter?: RowFilter;
}) {
  const Heading = headingLevel === 2 ? 'h2' : 'h3';
  return (
    <section className="card overflow-hidden rounded-lg border border-border bg-surface">
      <div className="border-b border-border px-4 py-2.5">
        <Heading className="text-sm font-semibold">{section.title}</Heading>
        {(collector?.notes ?? []).map((note, index) => (
          <p key={index} className="text-xs text-muted">
            {note}
          </p>
        ))}
      </div>
      {section.blocks.map((block) => {
        const anchor = `${section.collectorId}/${block.id}`;
        return (
          <Block
            key={block.id}
            block={block}
            collectorId={section.collectorId}
            headingLevel={headingLevel === 2 ? 3 : 4}
            showTitle={block.title !== section.title}
            revealItemId={reveal?.blockAnchor === anchor ? reveal.itemId : undefined}
            filter={filter}
          />
        );
      })}
    </section>
  );
}
