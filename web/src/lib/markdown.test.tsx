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

  it('renders lists, headings, bold and inline code', () => {
    const el = show('## Findings\n1. **Two** warnings\n2. Set `model`\n\n- a\n- b');

    expect([...el.querySelectorAll('ol li')].map((li) => li.textContent)).toEqual(['Two warnings', 'Set model']);
    expect(el.querySelector('ol strong')?.textContent).toBe('Two');
    expect(el.querySelector('ol code')?.textContent).toBe('model');
    expect([...el.querySelectorAll('ul li')].map((li) => li.textContent)).toEqual(['a', 'b']);
    expect(el.querySelector('p')?.textContent).toBe('Findings');
  });
});
