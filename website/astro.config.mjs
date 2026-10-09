// @ts-check
import { defineConfig, fontProviders } from 'astro/config';
import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import rehypeMermaid from 'rehype-mermaid';
import { remarkMermaidPre } from './plugins/remark-mermaid-pre.mjs';

const description =
  'Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.';

export default defineConfig({
  site: 'https://burgyn.github.io',
  base: '/MMLib.Alvo',
  trailingSlash: 'always',
  fonts: [
    {
      provider: fontProviders.fontsource(), name: 'Public Sans', cssVariable: '--font-public-sans',
      weights: [400, 500, 600, 700], styles: ['normal'], subsets: ['latin', 'latin-ext'],
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
      rehypePlugins: [[rehypeMermaid, { strategy: 'img-svg', dark: true }]],
    }),
  },
  vite: { server: { fs: { allow: ['..'] } } },
  integrations: [
    starlight({
      title: 'Alvo',
      description,
      logo: { src: './src/assets/alvo-mark.svg', alt: 'Alvo' },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/Burgyn/MMLib.Alvo' }],
      editLink: { baseUrl: 'https://github.com/Burgyn/MMLib.Alvo/edit/main/website/' },
      customCss: ['./src/styles/theme.css'],
      components: { Head: './src/components/Head.astro' },
      plugins: [starlightLinksValidator()],
      sidebar: [
        { label: 'Start here', items: ['start-here/why-alvo', 'start-here/quick-start', 'start-here/tutorial', 'start-here/embed'] },
        { label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
        { label: 'Concepts', items: [{ autogenerate: { directory: 'concepts' } }] },
        { label: 'Project', items: ['project/roadmap', 'project/changelog', 'project/contributing', 'project/license'] },
      ],
    }),
  ],
});
