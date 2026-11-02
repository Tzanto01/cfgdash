import { render } from '@testing-library/react';
import { Markdown, SUGGESTED_LABEL } from './markdown';

function show(text: string) {
  return render(<Markdown text={text} />).container;
}

describe('assistant markdown', () => {
  it('keeps raw HTML as literal text and never creates elements from it', () => {
    const el = show('Before <img src=x onerror="alert(1)"> and <script>alert(1)</script> after');

    expect(el.querySelector('img, script')).toBeNull();
    expect(el.textContent).toContain('<img src=x onerror="alert(1)">');
    expect(el.textContent).toContain('<script>alert(1)</script>');
  });

  it('renders images as their alt text and links as text with the full URL', () => {
    const el = show('See ![logo](https://example.com/logo.png) and [the docs](https://example.com/docs?a=1) or <https://example.com/raw>.');

    expect(el.querySelector('img, a')).toBeNull();
    expect(el.textContent).toBe('See [image: logo] and the docs (https://example.com/docs?a=1) or https://example.com/raw.');
  });

  it('labels code blocks as model output and keeps their text verbatim', () => {
    const el = show('Paste this:\n\n```md\n# Rules\n<b>keep</b>\n```\n');

    expect(el.querySelector('figcaption')?.textContent).toBe(SUGGESTED_LABEL);
    expect(el.querySelector('pre')?.textContent).toBe('# Rules\n<b>keep</b>');
    expect(el.querySelector('b')).toBeNull();
  });

  it('stays fast on model output built to make the inline patterns backtrack', () => {
    const started = performance.now();
    show(`${'[a]('.repeat(5000)} ${'<https://'.repeat(3000)} ${'**'.repeat(5000)}`);

    expect(performance.now() - started).toBeLessThan(1000);
  });

  it('closes a fence only on a bare fence line, so an inner ```bash line stays code', () => {
    const el = show('````\n```bash\nls\n```\n````\nafter');

    expect(el.querySelector('pre')?.textContent).toBe('```bash\nls\n```');
    expect(el.querySelectorAll('figure')).toHaveLength(1);
    expect(el.firstElementChild?.lastElementChild?.textContent).toBe('after');
  });

  it('keeps the number of an ordered list that resumes after a break', () => {
    const el = show('1. first\n\n```\nx\n```\n\n2. second');

    expect(el.querySelectorAll('ol')[1]?.getAttribute('start')).toBe('2');
  });

  it('shows a normalized URL and marks bidi and zero-width characters', () => {
    const el = show('[pay](  https://exаmple.com/a  ) safe\u202Etxt.exe');

    expect(el.textContent).toBe('pay (https://xn--exmple-4nf.com/a) safe\uFFFDtxt.exe');
  });

  it('renders lists, headings, bold and inline code', () => {
    const el = show('## Findings\n1. **Two** warnings\n2. Set `model`\n\n- a\n- b');

    expect([...el.querySelectorAll('ol li')].map((li) => li.textContent)).toEqual(['Two warnings', 'Set model']);
    expect(el.querySelector('ol strong')?.textContent).toBe('Two');
    expect(el.querySelector('ol code')?.textContent).toBe('model');
    expect([...el.querySelectorAll('ul li')].map((li) => li.textContent)).toEqual(['a', 'b']);
    expect(el.querySelector('p')?.textContent).toBe('Findings');
  });
});
