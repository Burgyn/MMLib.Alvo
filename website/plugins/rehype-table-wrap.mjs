import { visit } from 'unist-util-visit';

export function rehypeTableWrap() {
  return (tree) => {
    visit(tree, 'element', (node, index, parent) => {
      if (node.tagName !== 'table' || parent === undefined || index === undefined) return;
      if (parent.type === 'element' && parent.properties?.className?.includes('alvo-table-wrap')) return;
      parent.children[index] = { type: 'element', tagName: 'div', properties: { className: ['alvo-table-wrap'] }, children: [node] };
    });
  };
}
