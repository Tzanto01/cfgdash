import { render, screen } from '@testing-library/react';

it('renders', () => {
  render(<p>hello</p>);
  expect(screen.getByText('hello')).toBeTruthy();
});
