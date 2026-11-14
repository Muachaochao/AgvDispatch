export type UiTheme = 'dark' | 'light';

const themeKey = 'agv-ui-theme';

export function preferredTheme(): UiTheme {
  const saved = localStorage.getItem(themeKey);
  if (saved === 'light' || saved === 'dark') return saved;
  return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

export function applyUiTheme(theme: UiTheme) {
  document.documentElement.dataset.theme = theme;
  document.documentElement.classList.toggle('dark', theme === 'dark');
  localStorage.setItem(themeKey, theme);
}
