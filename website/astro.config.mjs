// @ts-check
import { readFileSync } from 'node:fs';
import { defineConfig, fontProviders } from 'astro/config';
import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import { ExpressiveCodeTheme } from '@astrojs/starlight/expressive-code';
import starlightLinksValidator from 'starlight-links-validator';
import starlightOpenAPI, { openAPISidebarGroups } from 'starlight-openapi';
import rehypeMermaid from 'rehype-mermaid';
import { remarkMermaidPre } from './plugins/remark-mermaid-pre.mjs';
import { rehypeTableWrap } from './plugins/rehype-table-wrap.mjs';

const description =
  'Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.';

const codeTheme = (name) =>
  ExpressiveCodeTheme.fromJSONString(readFileSync(new URL(`./src/styles/code-themes/${name}.json`, import.meta.url), 'utf8'));

const page = (label, slug) => ({ label, slug });

export default defineConfig({
  site: 'https://burgyn.github.io',
  base: '/MMLib.Alvo',
  trailingSlash: 'always',
  fonts: [
    {
      provider: fontProviders.fontsource(), name: 'Public Sans', cssVariable: '--font-public-sans',
      weights: [400, 500, 600, 700, 800], styles: ['normal'], subsets: ['latin', 'latin-ext'],
      fallbacks: ['system-ui', 'sans-serif'],
    },
    {
      provider: fontProviders.fontsource(), name: 'IBM Plex Mono', cssVariable: '--font-ibm-plex-mono',
      weights: [400, 500, 600], styles: ['normal'], subsets: ['latin', 'latin-ext'],
      fallbacks: ['ui-monospace', 'monospace'],
    },
  ],
  markdown: {
    processor: unified({
      remarkPlugins: [remarkMermaidPre],
      rehypePlugins: [rehypeTableWrap, [rehypeMermaid, { strategy: 'img-svg', dark: true }]],
    }),
  },
  vite: { server: { fs: { allow: ['..'] } } },
  integrations: [
    starlight({
      title: 'Alvo',
      description,
      logo: { light: './src/assets/alvo-mark.svg', dark: './src/assets/alvo-mark-on-dark.svg', alt: 'Alvo' },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/Burgyn/MMLib.Alvo' }],
      editLink: { baseUrl: 'https://github.com/Burgyn/MMLib.Alvo/edit/main/website/' },
      customCss: ['./src/styles/alvo-tokens.generated.css', './src/styles/theme.css'],
      expressiveCode: {
        themes: [codeTheme('alvo-dark'), codeTheme('alvo-light')],
        minSyntaxHighlightingColorContrast: 0,
        styleOverrides: {
          codeBackground: 'var(--codeBg)',
          borderColor: 'var(--alvo-code-border)',
          borderRadius: 'var(--radius-md)',
          codeFontFamily: 'var(--font-ibm-plex-mono)',
          codeFontSize: '0.8125rem',
          codeLineHeight: '1.7',
          uiFontFamily: 'var(--font-public-sans)',
          frames: {
            editorBackground: 'var(--codeBg)',
            terminalBackground: 'var(--codeBg)',
            editorTabBarBackground: 'var(--codeBg)',
            editorActiveTabBackground: 'var(--codeBg)',
            editorActiveTabForeground: 'var(--text)',
            editorActiveTabIndicatorTopColor: 'transparent',
            editorActiveTabIndicatorBottomColor: 'transparent',
            editorTabBarBorderBottomColor: 'var(--alvo-code-border)',
            terminalTitlebarBackground: 'var(--codeBg)',
            terminalTitlebarForeground: 'var(--dim)',
            terminalTitlebarBorderBottomColor: 'var(--alvo-code-border)',
            terminalTitlebarDotsForeground: 'var(--border2)',
            inlineButtonForeground: 'var(--dim)',
            inlineButtonBorder: 'var(--alvo-control-border)',
            frameBoxShadowCssValue: 'none',
          },
        },
      },
      components: {
        Head: './src/components/Head.astro',
        Header: './src/components/Header.astro',
        Banner: './src/components/Banner.astro',
        PageTitle: './src/components/PageTitle.astro',
      },
      plugins: [
        starlightOpenAPI([
          {
            base: 'reference/data-api',
            schema: './src/generated/openapi/data-api.json',
            sidebar: { label: 'Data API — example (vehicle-registry)' },
          },
        ]),
        starlightLinksValidator({ exclude: ['/MMLib.Alvo/reference/data-api/'] }),
      ],
      sidebar: [
        { label: 'Start here', items: [
          page('Why Alvo', 'start-here/why-alvo'),
          page('Quick start', 'start-here/quick-start'),
          page('Tutorial: your first backend', 'start-here/tutorial'),
          page('Run your own descriptor', 'start-here/run-your-own'),
          page('Embed in ASP.NET Core', 'start-here/embed'),
          page('For coding agents', 'start-here/coding-agents'),
          page('What works today', 'start-here/what-works-today'),
        ] },
        { label: 'Model your data', collapsed: true, items: [
          page('Entities and fields', 'guides/entities-and-fields'),
          page('Computed fields and rollups', 'guides/computed-and-rollups'),
          page('Indexes and uniqueness', 'guides/indexes'),
          page('Apply and evolve your descriptor', 'guides/apply-and-evolve'),
        ] },
        { label: 'Secure it', collapsed: true, items: [
          page('Authentication and API keys', 'guides/authentication'),
          page('Access rules', 'guides/access-rules'),
          page('Multi-tenancy', 'guides/multi-tenancy'),
        ] },
        { label: 'Add behaviour', collapsed: true, items: [
          page('Validate and transform writes (before-hooks)', 'guides/before-hooks'),
          page('After-hooks, events and webhooks', 'guides/after-hooks-and-webhooks'),
          page('Audit row changes', 'guides/audit-row-changes'),
        ] },
        { label: 'Use the API', collapsed: true, items: [
          page('Read data: filter, sort, page', 'guides/read-data'),
          page('Write data safely', 'guides/write-data'),
          page('Handle errors', 'guides/handle-errors'),
        ] },
        { label: 'Extend in C#', collapsed: true, items: [
          page('Use your own authentication', 'guides/own-authentication'),
          page('Call Alvo from your endpoints', 'guides/call-from-endpoints'),
          page('Custom CEL functions', 'guides/custom-cel-functions'),
        ] },
        { label: 'Operate', collapsed: true, items: [
          page('Running in production', 'guides/production'),
          page('The admin dashboard', 'guides/admin-dashboard'),
          page('The schema assistant', 'guides/schema-assistant'),
        ] },
        page('Examples', 'examples'),
        { label: 'Concepts', collapsed: true, items: [
          page('The project descriptor', 'concepts/descriptor'),
          page('CEL in Alvo', 'concepts/cel'),
          page('Security model', 'concepts/security-model'),
          page('Standalone and embedded', 'concepts/modes'),
          page('Architecture', 'concepts/architecture'),
          page('Dynamic entities (planned)', 'concepts/dynamic-entities'),
          page('Glossary', 'concepts/glossary'),
        ] },
        { label: 'Reference', collapsed: true, items: [
          page('Overview', 'reference'),
          { label: 'Descriptor schema', collapsed: true, items: [{ autogenerate: { directory: 'reference/descriptor' } }] },
          page('CEL functions', 'reference/cel-functions'),
          page('Problem types', 'reference/problem-types'),
          page('Data API conventions', 'data-api/conventions'),
          ...openAPISidebarGroups,
          page('Management API', 'reference/management-api'),
          page('Configuration keys', 'reference/configuration'),
          page('Limits and budgets', 'reference/limits'),
          page('Capabilities in this build', 'reference/capabilities'),
          { label: 'C# API', collapsed: true, items: [{ autogenerate: { directory: 'reference/csharp' } }] },
        ] },
        { label: 'Project', collapsed: true, items: [
          page('Roadmap and status', 'project/roadmap'),
          page('Changelog', 'project/changelog'),
          page('Contributing', 'project/contributing'),
          page('License', 'project/license'),
          page('FAQ', 'project/faq'),
        ] },
      ],
    }),
  ],
});
