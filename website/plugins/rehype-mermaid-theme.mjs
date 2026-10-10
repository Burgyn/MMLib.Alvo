import { visit } from 'unist-util-visit';

// rehype-mermaid's dark variant is a <picture> keyed on prefers-color-scheme, which ignores the site's theme switch,
// and its <img> has an empty alt. This turns each one into two images the site's data-theme shows one of, with the
// diagram's own accTitle and accDescr as the alt text. The figure scrolls sideways on a narrow screen rather than
// shrinking the diagram's text below reading size, so it is focusable and labelled (a scrollable region must be).
const svgText = (src) => decodeURIComponent(String(src).replace(/^data:image\/svg\+xml,/, ''));
const tagText = (svg, tag) => svg.match(new RegExp(`<${tag}[^>]*>([^<]*)</${tag}>`))?.[1]?.trim();

function labels(src) {
  const svg = svgText(src);
  return { title: tagText(svg, 'title'), desc: tagText(svg, 'desc') };
}

function image(src, width, height, alt, theme) {
  return {
    type: 'element',
    tagName: 'img',
    properties: {
      src, width, height, alt,
      className: ['alvo-mermaid__img', `alvo-mermaid__img--${theme}`],
      style: `min-width: min(${width}px, 44rem)`,
    },
    children: [],
  };
}

export function rehypeMermaidTheme() {
  return (tree) => {
    visit(tree, 'element', (node, index, parent) => {
      if (node.tagName !== 'picture' || parent === undefined || index === undefined) return;
      const source = node.children.find((c) => c.tagName === 'source' && String(c.properties.id ?? '').startsWith('mermaid-dark-'));
      const img = node.children.find((c) => c.tagName === 'img');
      if (!source || !img) return;
      const { title, desc } = labels(img.properties.src);
      if (!title || !desc) throw new Error('A Mermaid diagram needs both accTitle and accDescr: they become its alt text.');
      const alt = `${title}: ${desc}`;
      parent.children[index] = {
        type: 'element',
        tagName: 'figure',
        properties: { className: ['alvo-mermaid'], tabIndex: 0, role: 'group', ariaLabel: title },
        children: [
          image(img.properties.src, img.properties.width, img.properties.height, alt, 'light'),
          image(source.properties.srcSet ?? source.properties.srcset, source.properties.width, source.properties.height, alt, 'dark'),
        ],
      };
    });
  };
}
